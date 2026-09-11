using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using Autofac;
using Grpc.Core;
using Maple2.Database.Extensions;
using Maple2.Database.Storage;
using Maple2.Model;
using Maple2.Model.Enum;
using Maple2.Model.Error;
using Maple2.Model.Game;
using Maple2.Model.Game.Event;
using Maple2.Model.Metadata;
using Maple2.PacketLib.Tools;
using Maple2.Server.Core.Constants;
using Maple2.Server.Core.Network;
using Maple2.Server.Core.Packets;
using Maple2.Server.Core.Sync;
using Maple2.Server.Game.Commands;
using Maple2.Server.Game.Manager;
using Maple2.Server.Game.Manager.Field;
using Maple2.Server.Game.Manager.Items;
using Maple2.Server.Game.Model;
using Maple2.Server.Game.Packets;
using Maple2.Server.Game.Util;
using Maple2.Server.Game.Util.Sync;
using Maple2.Server.World.Service;
using Maple2.Tools.Extensions;
using Maple2.Tools.Scheduler;
using MigrationType = Maple2.Model.Enum.MigrationType;
using WorldClient = Maple2.Server.World.Service.World.WorldClient;

namespace Maple2.Server.Game.Session;

public sealed partial class GameSession : Core.Network.Session {
    protected override PatchType Type => PatchType.Ignore;
    public const int FIELD_KEY = 0x1234;

    // gameDisposeState: 0 = active, 1 = disposing, 2 = disposed
    private int gameDisposeState;
    private readonly GameServer server;
    private readonly Dictionary<int, short> activeClubBuffs = [];

    public readonly CommandRouter CommandHandler;
    public readonly EventQueue Scheduler;
    public DamageTracking? DamageTracking { get; set; }

    public int ServerTick;
    public int ClientTick;

    public int Latency;
    public string PlayerName => Player.Value.Character.Name;
    public Guid MachineId { get; private set; }
    private bool preMigrationSaved;
    private readonly object saveSync = new();
    private readonly object resetSync = new();
    private bool resetInitialized;
    private int pendingResetPeriods;
    private int persistenceAborted;
    private static readonly TimeSpan LockRpcTimeout = TimeSpan.FromSeconds(2);
    public bool PersistenceAborted => Volatile.Read(ref persistenceAborted) != 0;

    #region Autofac Autowired
    // ReSharper disable MemberCanBePrivate.Global
    // ReSharper disable UnusedAutoPropertyAccessor.Global
    public required GameStorage GameStorage { get; init; }
    public required WorldClient World { get; init; }
    public required ItemMetadataStorage ItemMetadata { get; init; }
    public required SkillMetadataStorage SkillMetadata { get; init; }
    public required TableMetadataStorage TableMetadata { get; init; }
    public required ServerTableMetadataStorage ServerTableMetadata { get; init; }
    public required MapMetadataStorage MapMetadata { get; init; }
    public required NpcMetadataStorage NpcMetadata { get; init; }
    public required AchievementMetadataStorage AchievementMetadata { get; init; }
    public required QuestMetadataStorage QuestMetadata { get; init; }
    public required ScriptMetadataStorage ScriptMetadata { get; init; }
    public required FunctionCubeMetadataStorage FunctionCubeMetadata { get; init; }
    public required RideMetadataStorage RideMetadata { get; init; }
    public required FieldManager.Factory FieldFactory { get; init; }
    public required ItemStatsCalculator ItemStatsCalc { private get; init; }
    public required PlayerInfoStorage PlayerInfo { get; init; }
    // ReSharper restore All
    // ReSharper restore UnusedAutoPropertyAccessor.Global
    #endregion

    public ConfigManager Config { get; set; } = null!;
    public MailManager Mail { get; set; } = null!;
    public GuildManager Guild { get; set; } = null!;
    public BuddyManager Buddy { get; set; } = null!;
    public ItemManager Item { get; set; } = null!;
    public HousingManager Housing { get; set; } = null!;
    public CurrencyManager Currency { get; set; } = null!;
    public MasteryManager Mastery { get; set; } = null!;
    public StatsManager Stats { get; set; } = null!;
    public BuffManager Buffs { get; set; } = null!;
    public ItemEnchantManager ItemEnchant { get; set; } = null!;
    public ItemMergeManager ItemMerge { get; set; } = null!;
    public ItemBoxManager ItemBox { get; set; } = null!;
    public BeautyManager Beauty { get; set; } = null!;
    public GameEventManager GameEvent { get; set; } = null!;
    public ExperienceManager Exp { get; set; } = null!;
    public AchievementManager Achievement { get; set; } = null!;
    public QuestManager Quest { get; set; } = null!;
    public ShopManager Shop { get; set; } = null!;
    public UgcMarketManager UgcMarket { get; set; } = null!;
    public BlackMarketManager BlackMarket { get; set; } = null!;
    public FieldManager? Field { get; set; }
    public FieldPlayer Player { get; private set; } = null!;
    public PartyManager Party { get; set; } = null!;
    public ConcurrentDictionary<int, GroupChatManager> GroupChats { get; set; }
    public ConcurrentDictionary<long, ClubManager> Clubs { get; set; }
    public SurvivalManager Survival { get; set; } = null!;
    public MarriageManager Marriage { get; set; } = null!;
    public FishingManager Fishing { get; set; } = null!;
    public DungeonManager Dungeon { get; set; } = null!;
    public AnimationManager Animation { get; set; } = null!;
    public RideManager Ride { get; set; } = null!;
    public MentoringManager Mentoring { get; set; } = null!;

    public GameSession(TcpClient tcpClient, GameServer server, IComponentContext context) : base(tcpClient) {
        this.server = server;
        State = SessionState.ChangeMap;
        CommandHandler = context.Resolve<CommandRouter>(new NamedParameter("session", this));
        Scheduler = new EventQueue(Logger);
        Scheduler.ScheduleRepeated(() => Send(TimeSyncPacket.Request()), TimeSpan.FromSeconds(1));

        OnLoop += Scheduler.InvokeAll;
        GroupChats = new ConcurrentDictionary<int, GroupChatManager>();
        Clubs = new ConcurrentDictionary<long, ClubManager>();
    }

    public override bool CanProcessPackets => base.CanProcessPackets && State != SessionState.Disconnected && !PersistenceAborted;

    public bool FindSession(long characterId, [NotNullWhen(true)] out GameSession? other) {
        return server.GetSession(characterId, out other);
    }

    public bool FindField(int mapId, [NotNullWhen(true)] out FieldManager? field) {
        field = server.GetField(mapId);
        return field is not null;
    }

    public bool EnterServer(long accountId, Guid machineId, MigrateInResponse migrateResponse) {
        long characterId = migrateResponse.CharacterId;
        int channel = migrateResponse.Channel;
        int mapId = migrateResponse.MapId;
        int portalId = migrateResponse.PortalId;
        long ownerId = migrateResponse.OwnerId;
        int roomId = migrateResponse.RoomId;
        var migrationType = (MigrationType) migrateResponse.Type;
        var position = new Vector3(migrateResponse.PositionX, migrateResponse.PositionY, migrateResponse.PositionZ);

        AccountId = accountId;
        CharacterId = characterId;
        MachineId = machineId;

        State = SessionState.ChangeMap;
        int objectId = FieldManager.NextGlobalId();
        GameStorage.Request? loadRequest = null;
        (LockRequest Request, long ExpiresAt)? lease = null;
        Player? player = null;
        try {
            lease = AcquireLock(AccountId, 5);
            loadRequest = GameStorage.Context();
            loadRequest.BeginTransaction();
            player = loadRequest.LoadPlayer(AccountId, CharacterId, objectId, GameServer.GetChannel());
            if (player == null) {
                throw new InvalidOperationException("Player data could not be loaded.");
            }
            EnsureLockValid(lease.Value.ExpiresAt);
            if (!loadRequest.Commit()) {
                throw new InvalidOperationException("Player loading could not be committed.");
            }
        } catch (Exception ex) {
            loadRequest?.Dispose();
            Logger.Error(ex, "Failed to load player from database: {AccountId}, {CharacterId}", AccountId, CharacterId);
            Send(MigrationPacket.MoveResult(MigrationError.s_move_err_default));
            return false;
        } finally {
            if (lease is { } acquired) {
                ReleaseLock(acquired.Request);
            }
        }

        using GameStorage.Request db = loadRequest!;
        Player = new FieldPlayer(this, player);
        server.OnConnected(this);
        Animation = new AnimationManager(this);
        Currency = new CurrencyManager(this);
        Mastery = new MasteryManager(this);
        Stats = new StatsManager(Player, ServerTableMetadata.UserStatTable);
        Config = new ConfigManager(db, this);
        Housing = new HousingManager(this, TableMetadata);
        Mail = new MailManager(this);
        ItemEnchant = new ItemEnchantManager(this);
        ItemMerge = new ItemMergeManager(this);
        ItemBox = new ItemBoxManager(this);
        Beauty = new BeautyManager(this);
        GameEvent = new GameEventManager(this);
        Exp = new ExperienceManager(this);
        Achievement = new AchievementManager(this);
        Quest = new QuestManager(this);
        Shop = new ShopManager(this);
        Guild = new GuildManager(this);
        Buddy = new BuddyManager(db, this);
        Item = new ItemManager(db, this, ItemStatsCalc);
        Buffs = new BuffManager(Player);
        UgcMarket = new UgcMarketManager(this);
        BlackMarket = new BlackMarketManager(this);
        Survival = new SurvivalManager(this);
        Marriage = new MarriageManager(this);
        Fishing = new FishingManager(this, TableMetadata, ServerTableMetadata);
        Dungeon = new DungeonManager(this);
        Ride = new RideManager(this);
        Mentoring = new MentoringManager(this);
        if (!CompleteResetInitialization()) {
            Send(MigrationPacket.MoveResult(MigrationError.s_move_err_default));
            return false;
        }
        CommandHandler.RegisterCommands(); // Refresh commands with proper permissions
        GroupChatInfoResponse groupChatInfoRequest = World.GroupChatInfo(new GroupChatInfoRequest {
            CharacterId = CharacterId,
        });

        foreach (GroupChatInfo groupChatInfo in groupChatInfoRequest.Infos) {
            var manager = new GroupChatManager(groupChatInfo, this);
            GroupChats.TryAdd(groupChatInfo.Id, manager);
        }

        int fieldId = mapId == 0 ? player.Character.MapId : mapId;
        if (!PrepareField(fieldId, out FieldManager? fieldManager, portalId: portalId, ownerId: ownerId, roomId: roomId, position: position)) {
            Send(MigrationPacket.MoveResult(MigrationError.s_move_err_default));
            return false;
        }
        switch (migrationType) {
            case MigrationType.DecorPlanner:
            case MigrationType.BlueprintDesigner:
                player.Home.EnterPlanner((PlotMode) migrationType);
                Housing.GetFieldPlot()?.SetPlannerMode((PlotMode) migrationType);
                break;
            case MigrationType.Dungeon:
                if (fieldManager is not DungeonFieldManager dungeonField) {
                    Logger.Error("Failed to load dungeon field for dungeon migration");
                    Send(MigrationPacket.MoveResult(MigrationError.s_move_err_default));
                    return false;
                }
                Dungeon.SetDungeon(dungeonField);
                break;
            case MigrationType.Normal:
            default:
                break;
        }

        var playerUpdate = new PlayerUpdateRequest {
            AccountId = accountId,
            CharacterId = characterId,
            LastOnlineTime = Player.Value.Character.LastOnlineTime,
        };
        playerUpdate.SetFields(UpdateField.All, player);
        playerUpdate.Health = new HealthUpdate {
            CurrentHp = Stats.Values[BasicAttribute.Health].Current,
            TotalHp = Stats.Values[BasicAttribute.Health].Total,
        };
        PlayerInfo.SendUpdate(playerUpdate);

        //session.Send(Packet.Of(SendOp.REQUEST_SYSTEM_INFO));
        Send(MigrationPacket.MoveResult(MigrationError.ok));

        Survival.Load();
        // MeretMarket
        // UserConditionEvent
        // PCBangBonus
        Guild.Load();
        foreach ((int id, GroupChatManager groupChat) in GroupChats) {
            groupChat.Load();
        }

        ClubInfoResponse clubInfoResponse = World.ClubInfo(new ClubInfoRequest {
            CharacterId = CharacterId,
        });

        foreach (ClubInfo clubInfo in clubInfoResponse.Clubs) {
            var manager = ClubManager.Create(clubInfo, this);
            if (manager == null) {
                Logger.Error("Failed to create club manager for club {ClubId}", clubInfo.Id);
                continue;
            }
            if (Clubs.TryAdd(clubInfo.Id, manager)) {
                manager.Load();
            }
        }

        Buddy.Load();

        try {
            PlayerConfigResponse configResponse = World.PlayerConfig(new PlayerConfigRequest {
                Get = new PlayerConfigRequest.Types.Get(),
                RequesterId = CharacterId,
            });
            long currentTick = Environment.TickCount64;
            Buffs.SetCacheBuffs(configResponse.Buffs, currentTick);
            Config.SetCacheSkillCooldowns(configResponse.SkillCooldowns, currentTick);
            Config.SetDeathPenalty(configResponse.DeathInfo, currentTick);

        } catch (RpcException ex) {
            Logger.Warning(ex, "Failed to load cache player config");
        }

        Send(SurvivalPacket.UpdateStats(player.Account));

        Send(TimeSyncPacket.Reset(DateTimeOffset.UtcNow));
        Send(TimeSyncPacket.Set(DateTimeOffset.UtcNow));

        Stats.Refresh();

        Send(RequestPacket.TickSync(Environment.TickCount));

        try {
            ChannelsResponse response = World.Channels(new ChannelsRequest());
            Send(ChannelPacket.Load(response.Channels));
        } catch (RpcException ex) {
            Logger.Warning(ex, "Failed to populate channels");
        }
        Send(ServerEnterPacket.Request(Player));

        // Ugc
        // Cash
        // Gvg
        // Pvp
        Send(StateSyncPacket.SyncNumber(0));
        // Load guide/first-timer records as early as possible so UI suppresses tips
        Send(GuideRecordPacket.Load(Config.GuideRecords));
        // SyncWorld
        Send(PrestigePacket.Load(player.Account));
        Send(PrestigePacket.LoadMissions(player.Account));
        Item.Inventory.Load();
        Item.Furnishing.Load();
        // Load Quests
        Quest.Load();
        // Send(QuestPacket.LoadSkyFortressMissions(Array.Empty<int>()));
        // Send(QuestPacket.LoadKritiasMissions(Array.Empty<int>()));
        // Send(QuestPacket.LoadQuests(Array.Empty<int>()));
        Achievement.Load();
        // MaidCraftItem
        // UserMaid
        // UserEnv
        Send(UserEnvPacket.LoadTitles(Player.Value.Unlock.Titles));
        Send(UserEnvPacket.InteractedObjects(Player.Value.Unlock.InteractedObjects));
        Send(UserEnvPacket.GatheringCounts(Config.GatheringCounts));
        Send(UserEnvPacket.LoadClaimedRewards(Player.Value.Unlock.MasteryRewardsClaimed));
        Send(FishingPacket.LoadAlbum(Player.Value.Unlock.FishAlbum.Values));
        Pet?.Load();
        Send(PetPacket.LoadCollection(Player.Value.Unlock.Pets));
        Send(LegionBattlePacket.Load(ServerTableMetadata.TimeEventTable.WorldBoss));
        try {
            TimeEventResponse bossResponse = World.TimeEvent(new TimeEventRequest {
                GetActiveWorldBosses = new TimeEventRequest.Types.GetActiveWorldBosses(),
            });
            if (bossResponse.ActiveWorldBosses.Count > 0) {
                var bossGroups = new List<ICollection<MapWorldBoss>>();
                foreach (TimeEventResponse.Types.ActiveWorldBoss active in bossResponse.ActiveWorldBosses) {
                    if (!ServerTableMetadata.TimeEventTable.WorldBoss.TryGetValue(active.MetadataId, out WorldBossMetadata? bossMetadata)) {
                        continue;
                    }
                    int bossNpcId = bossMetadata.NpcIds.Length > 0 ? bossMetadata.NpcIds[0] : 0;
                    int bossMapId = bossMetadata.TargetMapIds.Length > 0 ? bossMetadata.TargetMapIds[0] : 0;
                    bossGroups.Add(active.AliveChannels.Select(ch => new MapWorldBoss(bossNpcId, bossMapId, (short) ch, active.SpawnTimestamp)).ToList());
                }
                Send(WorldMapPacket.Load(bossGroups, []));
            }
        } catch (RpcException ex) {
            Logger.Warning(ex, "Failed to fetch active field bosses");
        }
        // CharacterAbility
        Config.LoadKeyTable();
        Send(GuideRecordPacket.Load(Config.GuideRecords));
        // DailyWonder*
        GameEvent.Load();
        Send(GameEventPacket.Load(server.GetEvents().ToArray()));
        Send(BannerListPacket.Load(server.GetSystemBanners()));
        Dungeon.Load();
        Send(InGameRankPacket.Load());
        Send(FieldEnterPacket.Request(Player));
        Party = new PartyManager(World, this);
        Send(HomeCommandPacket.LoadHome(AccountId));
        // ResponseCube
        // Mentor
        Send(MentorPacket.MyList());
        Send(MentorPacket.Unknown12());
        Config.LoadChatStickers();
        // Mail
        Mail.Notify(true);
        // BypassKey
        // AH
        Config.LoadWardrobe();

        // Online Notifications

        return true;
    }

    private void LeaveField(FieldManager? nextField = null) {
        FieldManager? previousField = Field;
        try {
            if (!PersistenceAborted && !DrainPostCommitCallbacks()) {
                throw new InvalidOperationException("Pending progression could not be applied before leaving the field.");
            }
            Array.Clear(ItemLockStaging);
            Array.Clear(DismantleStaging);
            DismantleOpened = false;
            Trade?.Dispose();
            Storage?.Dispose();
            Pet?.Dispose();
            Instrument = null;
            GuideObject = null;
            HeldCube = null;
            HeldLiftup = null;
            NpcScript = null;
            MiniGameRecord = null;

            if (!PersistenceAborted) {
                Buffs?.LeaveField();
                if (!DrainPostCommitCallbacks()) {
                    throw new InvalidOperationException("Field cleanup progression could not be applied.");
                }
            }
        } catch {
            AbortPersistence("Field departure cleanup did not complete.");
            throw;
        } finally {
            if (PersistenceAborted) {
                try {
                    Trade?.Dispose();
                } catch (Exception ex) {
                    Logger.Error(ex, "Failed to detach a quarantined trade for character {CharacterId}", CharacterId);
                }
                Trade = null;
                Storage = null;
                Pet = null;
            }
            try {
                if (previousField != null) {
                    try {
                        previousField.RemovePlayer(Player.ObjectId, out _);
                        if (!PersistenceAborted && !DrainPostCommitCallbacks()) {
                            throw new InvalidOperationException("Field departure progression could not be applied.");
                        }
                    } finally {
                        Scheduler.Stop();
                    }
                }
            } finally {
                if (previousField != nextField) {
                    previousField?.ReleaseAdmission(this);
                }
            }
        }
    }

    public bool PrepareField(int mapId, int portalId = -1, long ownerId = 0, int roomId = 0, in Vector3 position = default, in Vector3 rotation = default) {
        return PrepareFieldInternal(mapId, out _, portalId, ownerId, roomId, position, rotation);
    }

    public bool PrepareField(int mapId, [NotNullWhen(true)] out FieldManager? newField, int portalId = -1, long ownerId = 0, int roomId = 0, in Vector3 position = default, in Vector3 rotation = default) {
        return PrepareFieldInternal(mapId, out newField, portalId, ownerId, roomId, position, rotation);
    }

    private bool PrepareFieldInternal(int mapId, out FieldManager? newField, int portalId, long ownerId, int roomId, in Vector3 position, in Vector3 rotation) {
        // If entering home without instanceKey set, default to own home.
        if (mapId == Player.Value.Home.Indoor.MapId && ownerId == 0) {
            ownerId = AccountId;
        }

        if (Field is DungeonFieldManager dungeonField) {
            if (dungeonField.Lobby!.RoomFields.TryGetValue(mapId, out DungeonFieldManager? nextDungeonField)) {
                newField = nextDungeonField;
            } else if (mapId == dungeonField.Lobby.MapId) {
                newField = dungeonField.Lobby;
            } else {
                Migrate(mapId);
                newField = null;
                return false;
            }
        } else {
            newField = FieldFactory.Get(mapId, ownerId: ownerId, roomId: roomId);
            if (newField == null) {
                return false;
            }
        }

        if (!newField.TryReserveAdmission(this)) {
            Logger.Warning("Room {RoomId} cannot admit character {CharacterId}", newField.RoomId, CharacterId);
            newField = null;
            return false;
        }

        FieldManager destination = newField;
        bool prepared = false;
        try {
            State = SessionState.ChangeMap;
            LeaveField(destination);

            Field = destination;
            Player.Dispose();
            Player = Field.SpawnPlayer(this, Player, portalId, position, rotation);
            Config.Skill.UpdatePassiveBuffs();
            Player.Buffs.LoadFieldBuffs();
            Player.CheckRegen();
            prepared = true;
            return true;
        } finally {
            if (!prepared) {
                destination.ReleaseAdmission(this);
            }
        }
    }

    public bool EnterField() {
        if (Field == null) return false;
        if (!Field.TryReserveAdmission(this)) {
            Logger.Warning("Room {RoomId} cannot complete entry for character {CharacterId}", Field.RoomId, CharacterId);
            Send(FieldEnterPacket.Error(MigrationError.s_move_err_default));
            return false;
        }

        if (!Player.Value.Unlock.Maps.Contains(Player.Value.Character.MapId)) {
            if (Field.Metadata.Property.ExploreType > 0) {
                ExpType expType = Field.Metadata.Property.ExploreType == 1 ? ExpType.mapCommon : ExpType.mapHidden;
                Exp.AddExp(expType);
            }

            ConditionUpdate(ConditionType.explore_continent, codeLong: (int) Field.Metadata.Property.Continent);
            ConditionUpdate(ConditionType.continent, codeLong: (int) Field.Metadata.Property.Continent);
            ConditionUpdate(ConditionType.explore, codeLong: Field.MapId);
        }

        ConditionUpdate(ConditionType.map, codeLong: Field.MapId);

        Player.Value.Unlock.Maps.Add(Player.Value.Character.MapId);
        Config.LoadHotBars();
        Field.OnAddPlayer(Player);
        Scheduler.Start();
        State = SessionState.Connected;

        PlayerInfo.SendUpdate(new PlayerUpdateRequest {
            AccountId = AccountId,
            CharacterId = CharacterId,
            MapId = Field.MapId,
            Async = true,
        });

        Send(StatsPacket.Init(Player));
        Field.Broadcast(StatsPacket.Update(Player), Player.Session);

        //TODO: Save current hp/sp/ep in memory. This will help determine if player is dead upon login.
        var pWriter = Packet.Of(SendOp.UserState);
        pWriter.WriteInt(Player.ObjectId);
        pWriter.Write<ActorState>(ActorState.Fall);
        Send(pWriter);

        Send(EmotePacket.Load(Player.Value.Unlock.Emotes.Select(id => new Emote(id)).ToList()));
        Config.InitMacros();
        Config.LoadSkillCooldowns();
        Dungeon.LoadField();
        Marriage.Load();

        Send(CubePacket.DesignRankReward(Player.Value.Home));
        Send(CubePacket.UpdateProfile(Player, true));
        Send(CubePacket.ReturnMap(Player.Value.Character.ReturnMaps.Peek()));

        Config.LoadLapenshard();
        Config.LoadRevival();
        Config.LoadStatAttributes();
        Config.LoadSkillPoints();
        Player.Buffs.LoadFieldBuffs();
        RefreshClubBuffsInField();

        TimeEventResponse globalEventResponse = World.TimeEvent(new TimeEventRequest {
            GetGlobalPortal = new TimeEventRequest.Types.GetGlobalPortal(),
        });

        if (globalEventResponse.Error != 0 && globalEventResponse.GlobalPortalInfo != null) {
            if (ServerTableMetadata.TimeEventTable.GlobalPortal.TryGetValue(globalEventResponse.GlobalPortalInfo.MetadataId, out GlobalPortalMetadata? portal)) {
                Send(GlobalPortalPacket.Announce(portal, globalEventResponse.GlobalPortalInfo.EventId));
            }
        }

        Send(PremiumCubPacket.Activate(Player.ObjectId, Player.Value.Account.PremiumTime));
        Send(PremiumCubPacket.LoadItems(Player.Value.Account.PremiumRewardsClaimed));
        ConditionUpdate(ConditionType.map, codeLong: Player.Value.Character.MapId);
        ConditionUpdate(ConditionType.job_change, codeLong: (int) Player.Value.Character.Job.Code());

        if (!SessionSave()) {
            Send(NoticePacket.Disconnect(new InterfaceText("Your character could not be saved. Please reconnect.")));
            return false;
        }

        // Update the client with the latest channel list.
        ChannelsResponse response = World.Channels(new ChannelsRequest());
        Send(ChannelPacket.Load(response.Channels));
        Send(ServerListPacket.Load(Target.SERVER_NAME, [new IPEndPoint(Target.LoginIp, Target.LoginPort)], response.Channels));
        return true;
    }

    public void ReturnField() {
        Player player = Player.Value;
        if (player.Home.IsHomeSetup && !Player.Field.Plots.IsEmpty && (Player.Session.Housing.GetFieldPlot()?.IsPlanner ?? false)) {
            MigrateToPlanner(PlotMode.Normal);
            return;
        }

        Character character = player.Character;
        int mapId = character.ReturnMaps.IsEmpty
            ? Constant.DefaultReturnMapId
            : character.ReturnMaps.Peek();
        Vector3 position = character.ReturnPosition;

        if (!MapMetadata.TryGet(mapId, out _)) {
            mapId = Constant.DefaultReturnMapId;
            position = default;
        }

        character.ReturnPosition = default;

        if (character.MapId is Constant.DefaultHomeMapId) {
            Migrate(mapId);
            return;
        }

        if (Guild.Guild is not null) {
            TableMetadata.GuildTable.Houses.TryGetValue(Guild.Guild.HouseRank, out IReadOnlyDictionary<int, GuildTable.House>? houseRank);

            if (houseRank is not null) {
                houseRank.TryGetValue(Guild.Guild.HouseTheme, out GuildTable.House? house);

                if (house?.MapId == character.MapId) {
                    Migrate(mapId);
                    return;
                }
            }
        }

        // If returning to a map, pass in the spawn point.
        Send(PrepareField(mapId, position: position)
            ? FieldEnterPacket.Request(Player)
            : FieldEnterPacket.Error(MigrationError.s_move_err_default));
    }

    /// <summary>
    /// Updates game condition values for achievement and quest.
    /// </summary>
    /// <param name="conditionType">Condition Type to update</param>
    /// <param name="counter">Condition value to progress by. Default is 1.</param>
    /// <param name="targetString">condition target parameter in string.</param>
    /// <param name="targetLong">condition target parameter in long.</param>
    /// <param name="codeString">condition code parameter in string.</param>
    /// <param name="codeLong">condition code parameter in long.</param>
    public void ConditionUpdate(ConditionType conditionType, long counter = 1, string targetString = "", long targetLong = 0, string codeString = "", long codeLong = 0) {
        if (PersistenceAborted) {
            return;
        }
        Achievement.Update(conditionType, counter, targetString, targetLong, codeString, codeLong);
        Quest.Update(conditionType, counter, targetString, targetLong, codeString, codeLong);
    }

    public IList<GameEvent> FindEvent(GameEventType type) => server.FindEvent(type);

    public GameEvent? FindEvent(int id) => server.FindEvent(id);
    public IEnumerable<GameEvent> Events => server.GetEvents();

    public IEnumerable<PremiumMarketItem> GetPremiumMarketItems(params int[] tabIds) => server.GetPremiumMarketItems(tabIds);

    public PremiumMarketItem? GetPremiumMarketItem(int id, int subId = 0) => server.GetPremiumMarketItem(id, subId);

    public RewardRecord GetRewardContent(int id) {
        if (!TableMetadata.RewardContentTable.BaseEntries.TryGetValue(id, out RewardContentTable.Base? baseMetadata)) {
            return new RewardRecord();
        }

        long exp = 0;
        if (baseMetadata.ExpTableId > 0) {
            if (baseMetadata.ExpTableId > 100000 && TableMetadata.RewardContentTable.ExpStaticEntries.TryGetValue(baseMetadata.ExpTableId, out exp)) {
                // Static exp entry found
            } else if (TableMetadata.ExpTable.ExpBase.TryGetValue(baseMetadata.ExpTableId, out IReadOnlyDictionary<int, long>? expTable)) {
                exp = expTable[Player.Value.Character.Level];
            }
            if (baseMetadata.ExpFactor > 0f) {
                exp = (long) (exp * baseMetadata.ExpFactor);
            }
            Exp.AddExp(exp);
        }

        long meso = 0;
        if (baseMetadata.MesoTableId > 0) {
            if (baseMetadata.MesoTableId > 100000 && TableMetadata.RewardContentTable.MesoStaticEntries.TryGetValue(baseMetadata.MesoTableId, out meso)) {
                // Static meso entry found
            } else if (TableMetadata.RewardContentTable.MesoEntries.TryGetValue(baseMetadata.MesoTableId, out Dictionary<int, long>? mesoTable)) {
                if (!mesoTable.TryGetValue(Player.Value.Character.Level, out meso)) {
                    Logger.Error("Failed to find meso for level {Level} in table {TableId}", Player.Value.Character.Level, baseMetadata.MesoTableId);
                }
            }
            if (baseMetadata.MesoFactor > 0f) {
                meso = (long) (meso * baseMetadata.MesoFactor);
            }
            Currency.Meso += meso;
        }

        long prestigeExp = 0;
        if (baseMetadata.PrestigeExpTableId > 0 && ServerTableMetadata.PrestigeIdExpTable.Entries.TryGetValue(baseMetadata.PrestigeExpTableId, out PrestigeIdExpTable.Entry? prestigeExpTable)) {
            // TODO: Prestige exp given this way is fixed. Need to revise how exp is given.
        }

        List<Item> items = [];
        if (baseMetadata.ItemTableId > 0 && TableMetadata.RewardContentTable.ItemEntries.TryGetValue(baseMetadata.ItemTableId, out RewardContentTable.Item? itemMetadata)) {
            foreach (RewardContentTable.Item.Data data in itemMetadata.ItemData) {
                if ((data.MinLevel > 0 && Player.Value.Character.Level < data.MinLevel) ||
                    (data.MaxLevel > 0 && Player.Value.Character.Level > data.MaxLevel)) {
                    continue;
                }
                foreach (RewardItem rewardItem in data.RewardItems) {
                    Item? item = Field?.ItemDrop.CreateItem(rewardItem.ItemId, rewardItem.Rarity, rewardItem.Amount);
                    if (item != null) {
                        items.Add(item);
                    }
                }
            }
            foreach (Item item in items) {
                if (!Item.Inventory.Add(item, true)) {
                    Item.MailItem(item);
                }
            }
        }
        return new RewardRecord(items, exp, prestigeExp, meso);
    }

    public void ChannelBroadcast(ByteWriter packet) {
        server.Broadcast(packet);
    }

    public bool DailyReset() {
        return Reset(ResetType.Day);
    }

    public bool WeeklyReset() {
        return Reset(ResetType.Week);
    }

    public bool MonthlyReset() {
        return Reset(ResetType.Month);
    }

    private bool CompleteResetInitialization() {
        int pending;
        lock (resetSync) {
            resetInitialized = true;
            pending = pendingResetPeriods;
            pendingResetPeriods = 0;
        }

        bool success = true;
        foreach (ResetType type in new[] { ResetType.Day, ResetType.Week, ResetType.Month }) {
            if ((pending & (1 << (int) type)) != 0) {
                success &= Reset(type);
            }
        }
        return success;
    }

    private bool Reset(ResetType type) {
        // Do not acquire Item under this gate; initialization drains queued resets before gameplay.
        lock (resetSync) {
            if (!resetInitialized) {
                pendingResetPeriods |= 1 << (int) type;
                Logger.Information("Deferring {Reset} reset until character {CharacterId} finishes loading", type, CharacterId);
                return false;
            }
        }
        if (Item == null || Player == null || Config == null || Shop == null || Dungeon == null) {
            Logger.Warning("Cannot apply {Reset} reset before character {CharacterId} finishes loading", type, CharacterId);
            return false;
        }
        if (!DrainPostCommitCallbacks()) {
            return false;
        }

        var packets = new List<ByteWriter>();
        lock (Item) {
            lock (saveSync) {
                if (PersistenceAborted || preMigrationSaved) {
                    Logger.Warning("Cannot apply {Reset} reset to unavailable character {CharacterId}", type, CharacterId);
                    return false;
                }
                (LockRequest Request, long ExpiresAt)? lease = null;
                try {
                    lease = AcquireLock(AccountId, 5);
                    switch (type) {
                        case ResetType.Day:
                            Config.GatheringCounts.Clear();
                            Config.InstantReviveCount = 0;
                            Player.Value.Account.PremiumRewardsClaimed.Clear();
                            Player.Value.Account.PrestigeExp = Player.Value.Account.PrestigeCurrentExp;
                            Player.Value.Account.PrestigeLevelsGained = 0;
                            foreach (PrestigeMission mission in Player.Value.Account.PrestigeMissions) {
                                mission.GainedLevels = 0;
                                mission.Awarded = false;
                            }
                            Player.Value.Home.DecorationRewardTimestamp = 0;
                            Player.Value.Account.MesoMarketListed = 0;
                            Shop.DailyReset();
                            Dungeon.ResetDailyClears();
                            break;
                        case ResetType.Week:
                            Player.Value.Account.PrestigeRewardsClaimed.Clear();
                            Dungeon.ResetWeeklyClears();
                            Shop.WeeklyReset();
                            break;
                        case ResetType.Month:
                            Player.Value.Account.MesoMarketPurchased = 0;
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown reset period.");
                    }

                    Save(lease.Value.ExpiresAt);
                    if (PersistenceAborted) {
                        return false;
                    }
                    if (type == ResetType.Day) {
                        packets.Add(UserEnvPacket.GatheringCounts(Config.GatheringCounts));
                        packets.Add(RevivalPacket.RevivalCount(Config.InstantReviveCount));
                        packets.Add(PremiumCubPacket.LoadItems(Player.Value.Account.PremiumRewardsClaimed));
                        packets.Add(CubePacket.DesignRankReward(Player.Value.Home));
                    }
                    if (type is ResetType.Day or ResetType.Week) {
                        packets.Add(PrestigePacket.Load(Player.Value.Account));
                    }
                    if (type is ResetType.Day or ResetType.Month) {
                        packets.Add(MesoMarketPacket.Quota(Player.Value.Account.MesoMarketListed, Player.Value.Account.MesoMarketPurchased));
                    }
                } catch (Exception ex) {
                    if (lease != null) {
                        AbortPersistence("Scheduled reset could not be committed; reload is required.");
                    }
                    Logger.Error(ex, "Failed {Reset} reset for account {AccountId}, character {CharacterId}", type, AccountId, CharacterId);
                    return false;
                } finally {
                    if (lease is { } acquired) {
                        ReleaseLock(acquired.Request);
                    }
                }
            }
        }
        foreach (ByteWriter packet in packets) {
            Send(packet);
        }
        if (type is ResetType.Day or ResetType.Week) {
            try {
                Dungeon.NotifyReset(type == ResetType.Week);
            } catch (RpcException ex) {
                Logger.Warning(ex, "{Reset} reset committed but dungeon notification failed for character {CharacterId}", type, CharacterId);
            }
        }
        return true;
    }

    public void RefreshClubBuffs() {
        FieldManager? field = Field;
        if (field == null) {
            return;
        }

        HashSet<long> fieldCharacterIds = field.Players.Values
            .Select(fieldPlayer => fieldPlayer.Value.Character.Id)
            .ToHashSet();
        Dictionary<int, short> desired = ClubBuffPolicy.SelectEffects(
            Clubs.Values.Select(manager => new ClubBuffPolicy.ClubSelection(
                manager.Club.State,
                manager.Club.BuffId,
                manager.Club.Members.Keys.ToArray())),
            CharacterId,
            fieldCharacterIds,
            TableMetadata.ClubBuffTable.Entries);
        foreach ((int buffId, short level) in activeClubBuffs.ToArray()) {
            if (!Player.Buffs.HasBuff(buffId, level)) {
                activeClubBuffs.Remove(buffId);
            }
        }
        ClubBuffPolicy.Changes changes = ClubBuffPolicy.GetChanges(activeClubBuffs, desired);

        foreach (int buffId in changes.Remove) {
            Player.Buffs.Remove(buffId, Player.ObjectId);
            activeClubBuffs.Remove(buffId);
        }
        foreach (ClubBuffPolicy.Effect buff in changes.Add) {
            Player.Buffs.AddBuff(Player, Player, buff.Id, buff.Level, field.FieldTick);
            if (Player.Buffs.HasBuff(buff.Id, buff.Level)) {
                activeClubBuffs[buff.Id] = buff.Level;
            }
        }
    }

    public void RefreshClubBuffsInField() {
        if (Field == null) {
            return;
        }
        foreach (FieldPlayer player in Field.Players.Values) {
            player.Session.RefreshClubBuffs();
        }
    }

    public void ScheduleClubBuffRefresh() {
        FieldManager? field = Field;
        field?.Scheduler.Schedule(() => {
            if (ReferenceEquals(Field, field)) {
                RefreshClubBuffs();
            }
        });
    }

    public void MigrateToPlanner(PlotMode plotMode) {
        if (!MigrationSave()) {
            Send(MigrationPacket.GameToGameError(MigrationError.s_move_err_default));
            return;
        }
        try {
            var request = new MigrateOutRequest {
                AccountId = AccountId,
                CharacterId = CharacterId,
                MachineId = MachineId.ToString(),
                Server = Server.World.Service.Server.Game,
                MapId = Constant.DefaultHomeMapId,
                OwnerId = AccountId,
                InstancedContent = true,
                Type = (World.Service.MigrationType) plotMode,
                RoomId = plotMode is not PlotMode.Normal ? -1 : 0,
            };

            MigrateOutResponse response = World.MigrateOut(request);
            var endpoint = new IPEndPoint(IPAddress.Parse(response.IpAddress), response.Port);
            Send(MigrationPacket.GameToGame(endpoint, response.Token, Constant.DefaultHomeMapId));
            State = SessionState.ChangeMap;
        } catch (RpcException ex) {
            Send(MigrationPacket.GameToGameError(MigrationError.s_move_err_default));
            Send(NoticePacket.Disconnect(new InterfaceText(ex.Message)));
        } finally {
            Disconnect();
        }
    }

    public void Migrate(int mapId, long ownerId = 0) {
        bool isInstanced = ServerTableMetadata.InstanceFieldTable.Entries.ContainsKey(mapId);
        int previousMap = Player.Value.Character.MapId;
        short previousReturnChannel = Player.Value.Character.ReturnChannel;
        if (isInstanced) {
            Player.Value.Character.ReturnChannel = Player.Value.Character.Channel;
        } else {
            Player.Value.Character.MapId = mapId;
            Player.Value.Character.ReturnChannel = 0;
        }
        if (!MigrationSave()) {
            Player.Value.Character.MapId = previousMap;
            Player.Value.Character.ReturnChannel = previousReturnChannel;
            Send(MigrationPacket.GameToGameError(MigrationError.s_move_err_default));
            return;
        }

        try {
            var request = new MigrateOutRequest {
                AccountId = AccountId,
                CharacterId = CharacterId,
                MachineId = MachineId.ToString(),
                Server = Server.World.Service.Server.Game,
                MapId = mapId,
                OwnerId = ownerId,
                InstancedContent = isInstanced,
            };

            MigrateOutResponse response = World.MigrateOut(request);
            var endpoint = new IPEndPoint(IPAddress.Parse(response.IpAddress), response.Port);
            Send(MigrationPacket.GameToGame(endpoint, response.Token, mapId));

            State = SessionState.ChangeMap;
        } catch (RpcException ex) {
            Send(MigrationPacket.GameToGameError(MigrationError.s_move_err_default));
            Send(NoticePacket.Disconnect(new InterfaceText(ex.Message)));
        } finally {
            Disconnect();
        }
    }

    private (LockRequest Request, long ExpiresAt) AcquireLock(long accountId, int maxRetries = 3) {
        const int backoffMs = 500;
        var request = new LockRequest {
            AccountId = accountId,
            OwnerToken = Guid.NewGuid().ToString("N"),
        };
        for (int attempt = 0; attempt < maxRetries; attempt++) {
            try {
                LockResponse response = World.AcquireLock(request, deadline: DateTime.UtcNow.Add(LockRpcTimeout));
                if (string.IsNullOrEmpty(response.Error) && response.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) {
                    return (request, response.ExpiresAt);
                }
            } catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded) {
                Logger.Warning(ex, "Account lock acquisition failed for {AccountId}", accountId);
            }
            if (attempt + 1 < maxRetries) {
                Thread.Sleep(backoffMs);
            }
        }

        Logger.Error("Failed to acquire lock for account {AccountId} after {MaxRetries} retries", accountId, maxRetries);
        throw new RpcException(new Status(StatusCode.Unavailable, "Account persistence is busy. Please try again."));
    }

    private static void EnsureLockValid(long expiresAt) {
        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= expiresAt) {
            throw new RpcException(new Status(StatusCode.DeadlineExceeded, "Account persistence lease expired."));
        }
    }

    private static void RequireSaveSuccess(bool saved, string component, long leaseExpiresAt) {
        EnsureLockValid(leaseExpiresAt);
        if (!saved) {
            throw new InvalidOperationException($"Failed to save {component} state.");
        }
    }

    private void ReleaseLock(LockRequest lease) {
        try {
            LockResponse response = World.ReleaseLock(lease, deadline: DateTime.UtcNow.Add(LockRpcTimeout));
            if (!string.IsNullOrEmpty(response.Error)) {
                Logger.Warning("Failed to release lock for account {AccountId}: {ErrorMessage}", lease.AccountId, response.Error);
            }
        } catch (RpcException ex) {
            Logger.Error(ex, "Failed to release lock for account {AccountId}", lease.AccountId);
        }
    }

    #region Dispose
    ~GameSession() => Dispose(false);

    protected override void Dispose(bool disposing) {
        // Ensure dispose is only run once
        if (Interlocked.CompareExchange(ref gameDisposeState, 1, 0) != 0) return;

        // If login never completed, Player is null — skip game-level cleanup.
        if (Player is null) {
            base.Dispose(disposing);
            Interlocked.Exchange(ref gameDisposeState, 2);
            return;
        }

        // Snapshot values needed after teardown
        long fieldTickSnapshot = Field?.FieldTick ?? Environment.TickCount64;

        try {
            if (OnLoop != null) OnLoop -= Scheduler.InvokeAll;
            if (!PersistenceAborted && !DrainPostCommitCallbacks()) {
                throw new InvalidOperationException("Pending progression could not be applied before disconnect.");
            }
            if (State == SessionState.Connected) {
                PlayerInfo.SendUpdate(new PlayerUpdateRequest {
                    AccountId = AccountId,
                    CharacterId = CharacterId,
                    LastOnlineTime = DateTime.UtcNow.ToEpochSeconds(),
                    MapId = 0,
                    Channel = -1,
                    Async = false,
                });

                Party.CheckDisband();
            }
            server.OnDisconnected(this);
            LeaveField();
            Scheduler.Stop();
            Player.Value.Character.Channel = -1;
            Player.Value.Account.Online = false;
            State = SessionState.Disconnected;

            // Early base dispose to stop further network sends
            base.Dispose(disposing);

            if (!PersistenceAborted) {
                if (MigrationSave()) {
                    SaveCacheConfig();
                } else {
                    Logger.Error("Disconnected session could not be persisted for account {AccountId}, character {CharacterId}", AccountId, CharacterId);
                }
            }
        } catch (Exception ex) {
            AbortPersistence("Session teardown persistence did not complete.");
            Logger.Error(ex, "Error during session cleanup for {Player}", PlayerName);
        } finally {
            State = SessionState.Disconnected;
            try { base.Dispose(disposing); } catch (Exception ex) { Logger.Error(ex, "Error closing session for {Player}", PlayerName); }
            SafeDispose(Guild);
            SafeDispose(Buddy);
            SafeDispose(Party);
            foreach ((int groupChatId, GroupChatManager groupChat) in GroupChats) {
                try { groupChat.CheckDisband(); } catch (Exception ex) { Logger.Error(ex, "Error disbanding group chat {Id} for {Player}", groupChatId, PlayerName); }
            }
            foreach ((long clubId, ClubManager club) in Clubs) {
                SafeDispose(club);
            }
            try { Player.Dispose(); } catch (Exception ex) { Logger.Error(ex, "Error disposing player for {Player}", PlayerName); }
            Interlocked.Exchange(ref gameDisposeState, 2);
        }
        return;

        void SafeDispose(IDisposable? disp) {
            if (disp == null) return;
            try { disp.Dispose(); } catch (Exception ex) { Logger.Error(ex, "Error disposing component for {Player}", PlayerName); }
        }

        void SaveCacheConfig() {
            List<Buff> buffs = Buffs.GetSaveCacheBuffs();
            IList<SkillCooldown> skillCooldowns = Config.GetCurrentSkillCooldowns();

            long stopTime = DateTime.Now.ToEpochSeconds();
            long fieldTick = fieldTickSnapshot;

            try {
                PlayerConfigResponse _ = World.PlayerConfig(new PlayerConfigRequest {
                    Save = new PlayerConfigRequest.Types.Save {
                        Buffs = {
                            buffs
                                .Where(buff => buff.EndTick - fieldTick > 0)
                                .Select(buff => new BuffInfo {
                                    Id = buff.Id,
                                    Level = buff.Level,
                                    MsRemaining = (buff.EndTick - fieldTick).Truncate32(),
                                    Stacks = buff.Stacks,
                                    Enabled = buff.Enabled,
                                    StopTime = stopTime,
                                }),
                        },
                        SkillCooldowns = {
                            skillCooldowns
                                .Where(cooldown => cooldown.EndTick - fieldTick > 0)
                                .Select(cooldown => new SkillCooldownInfo {
                                    SkillId = cooldown.SkillId,
                                    SkillLevel = cooldown.Level,
                                    GroupId = cooldown.GroupId,
                                    MsRemaining = (cooldown.EndTick - fieldTick).Truncate32(),
                                    StopTime = stopTime,
                                    Charges = cooldown.Charges,
                                }),
                        },
                        DeathInfo = new DeathInfo {
                            Count = Config.DeathCount,
                            MsRemaining = (int) (Config.DeathPenaltyEndTick - fieldTick),
                            StopTime = stopTime,
                        },
                    },
                    RequesterId = CharacterId,
                });
            } catch (Exception ex) { Logger.Error(ex, "Error saving buffs for {Player}", PlayerName); }
        }
    }
    #endregion

    public override string ToString() {
        return $"GameSession: {Player?.Value?.Character?.Name} ({CharacterId}) [{State}] // Account: {AccountId}, Machine: {MachineId}, Channel: {Player?.Value?.Character?.Channel}, Map: {Player?.Value?.Character?.MapId}]";
    }

    public void AbortPersistence(string reason) {
        if (Interlocked.Exchange(ref persistenceAborted, 1) == 0) {
            Logger.Error("Persistence aborted for account {AccountId}, character {CharacterId}: {Reason}", AccountId, CharacterId, reason);
            if (State != SessionState.Disconnected && Scheduler != null && Volatile.Read(ref gameDisposeState) == 0) {
                _ = Task.Run(() => {
                    try {
                        // Wait for the committing operation to release Item, then disconnect without holding it.
                        if (Item != null) {
                            lock (Item) { }
                        }
                        Send(NoticePacket.Disconnect(new InterfaceText("Session data could not be confirmed. Please log in again.")));
                        Disconnect();
                    } catch (Exception ex) {
                        Logger.Error(ex, "Failed to disconnect quarantined character {CharacterId}", CharacterId);
                    }
                });
            }
        }
    }

    public bool MigrationSave() {
        if (Item == null) {
            Logger.Error("Cannot save character {CharacterId} before inventory loading completes", CharacterId);
            return false;
        }
        if (PersistenceAborted) {
            return false;
        }
        if (!DrainPostCommitCallbacks()) {
            return false;
        }
        try {
            // Trade uses its own mutex before participant item locks.
            Trade?.Dispose();
            lock (Item) {
                Storage?.Dispose();
                Pet?.Dispose();
            }
        } catch (Exception ex) {
            AbortPersistence("Item state could not be closed before migration.");
            Logger.Error(ex, "Failed to close item sessions before migrating character {CharacterId}", CharacterId);
            return false;
        }
        if (!DrainPostCommitCallbacks()) {
            return false;
        }
        lock (Item) {
            lock (saveSync) {
                if (PersistenceAborted) {
                    return false;
                }
                if (preMigrationSaved) {
                    return true;
                }
                preMigrationSaved = SessionSave();
                return preMigrationSaved && !PersistenceAborted;
            }
        }
    }

    private bool DrainPostCommitCallbacks() {
        if (PersistenceAborted) {
            return false;
        }
        if ((Item != null && Monitor.IsEntered(Item)) || Monitor.IsEntered(saveSync)) {
            Logger.Warning("Cannot finalize character {CharacterId} while persistence locks are held", CharacterId);
            return false;
        }
        try {
            Scheduler.DrainImmediate();
            return !PersistenceAborted;
        } catch (Exception ex) {
            AbortPersistence("Deferred progression could not be completed before the final snapshot.");
            Logger.Error(ex, "Failed to finish pending callbacks for character {CharacterId}", CharacterId);
            return false;
        }
    }

    public bool SessionSave() {
        if (Item == null) {
            Logger.Error("Cannot save character {CharacterId} before inventory loading completes", CharacterId);
            return false;
        }
        lock (Item) {
            lock (saveSync) {
                if (PersistenceAborted) {
                    return false;
                }
                if (preMigrationSaved) {
                    Logger.Warning("Save rejected after migration was prepared for character {CharacterId}", CharacterId);
                    return false;
                }
                (LockRequest Request, long ExpiresAt)? lease = null;
                try {
                    lease = AcquireLock(AccountId, 5);
                    Save(lease.Value.ExpiresAt);
                    return !PersistenceAborted;
                } catch (Exception ex) {
                    Logger.Error(ex, "SessionSave failed AccountId={AccountId} CharacterId={CharacterId}", AccountId, CharacterId);
                    return false;
                } finally {
                    if (lease is { } acquired) {
                        ReleaseLock(acquired.Request);
                    }
                }
            }
        }
    }

    // Don't call this directly, since it does not acquire the account lock.
    private void Save(long leaseExpiresAt) {
        var timestamps = (Player.Value.Account.LastModified, Player.Value.Character.LastModified,
            Player.Value.Unlock.LastModified, Player.Value.Home.LastModified);
        bool committed = false;
        try {
            using GameStorage.Request db = GameStorage.Context();
            db.BeginTransaction();
            EnsureLockValid(leaseExpiresAt);
            bool playerSaved = db.SavePlayer(Player);
            if (!playerSaved) {
                AbortPersistence("Player snapshot was rejected; reload is required.");
            }
            RequireSaveSuccess(playerSaved, nameof(Player), leaseExpiresAt);
            RequireSaveSuccess(UgcMarket.Save(db), nameof(UgcMarket), leaseExpiresAt);
            RequireSaveSuccess(Config.Save(db), nameof(Config), leaseExpiresAt);
            RequireSaveSuccess(Shop.Save(db), nameof(Shop), leaseExpiresAt);
            RequireSaveSuccess(Item.Save(db), nameof(Item), leaseExpiresAt);
            RequireSaveSuccess(Survival.Save(db), nameof(Survival), leaseExpiresAt);
            RequireSaveSuccess(Housing.Save(db), nameof(Housing), leaseExpiresAt);
            RequireSaveSuccess(GameEvent.Save(db), nameof(GameEvent), leaseExpiresAt);
            RequireSaveSuccess(Achievement.Save(db), nameof(Achievement), leaseExpiresAt);
            RequireSaveSuccess(Quest.Save(db), nameof(Quest), leaseExpiresAt);
            RequireSaveSuccess(Dungeon.Save(db), nameof(Dungeon), leaseExpiresAt);
            RequireSaveSuccess(db.SaveChanges(), "pending changes", leaseExpiresAt);
            if (PersistenceAborted) {
                throw new InvalidOperationException("The session was quarantined during persistence.");
            }
            EnsureLockValid(leaseExpiresAt);
            try {
                if (!db.Commit()) {
                    throw new InvalidOperationException("Failed to commit session state.");
                }
            } catch {
                AbortPersistence("Session transaction commit could not be confirmed.");
                throw;
            }
            committed = true;
        } finally {
            if (!committed) {
                (Player.Value.Account.LastModified, Player.Value.Character.LastModified,
                    Player.Value.Unlock.LastModified, Player.Value.Home.LastModified) = timestamps;
            }
        }
    }
}
