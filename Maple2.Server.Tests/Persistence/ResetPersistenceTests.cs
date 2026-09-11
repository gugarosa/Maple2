using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Maple2.Database.Context;
using Maple2.Database.Storage;
using Maple2.Model.Enum;
using Maple2.Model.Game;
using Maple2.Model.Game.Dungeon;
using Maple2.Model.Game.Shop;
using Maple2.Model.Metadata;
using Maple2.Server.Core.Constants;
using Maple2.Server.Game;
using Maple2.Server.Game.Manager;
using Maple2.Server.Game.Manager.Items;
using Maple2.Server.Game.Model;
using Maple2.Server.Game.Session;
using Maple2.Server.World.Service;
using Maple2.Tools.Scheduler;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Serilog;
using GuildQuestRewardsMigration = Maple2.Server.World.Migrations.GuildQuestRewards;
using NetworkSession = Maple2.Server.Core.Network.Session;
using WorldClient = Maple2.Server.World.Service.World.WorldClient;

namespace Maple2.Server.Tests.Persistence;

[Explicit("Requires MAPLE2_RUN_DB_TESTS=1 and isolated validation metadata.")]
[NonParallelizable]
public class ResetPersistenceTests {
    private MetadataContext metadata = null!;
    private DbContextOptions options = null!;
    private GameStorage storage = null!;
    private ItemMetadataStorage items = null!;
    private TableMetadataStorage tables = null!;
    private ServerTableMetadataStorage serverTables = null!;
    private bool databaseCreated;

    [OneTimeSetUp]
    public void CreateIsolatedDatabase() {
        if (Environment.GetEnvironmentVariable("MAPLE2_RUN_DB_TESTS") != "1") {
            Assert.Ignore("Set MAPLE2_RUN_DB_TESTS=1 to run isolated persistence tests.");
        }
        string metadataDatabase = Required("DATA_DB_NAME");
        if (!metadataDatabase.StartsWith("maple2_validation_", StringComparison.Ordinal)) {
            throw new InvalidOperationException("Reset tests require a maple2_validation_ metadata database.");
        }
        var connection = new MySqlConnectionStringBuilder {
            Server = Required("DB_IP"),
            Port = uint.Parse(Required("DB_PORT"), CultureInfo.InvariantCulture),
            UserID = Required("DB_USER"),
            Password = Required("DB_PASSWORD"),
            Database = metadataDatabase,
            OldGuids = true,
        };
        ServerVersion version = ServerVersion.AutoDetect(connection.ConnectionString);
        metadata = new MetadataContext(new DbContextOptionsBuilder().UseMySql(connection.ConnectionString, version).Options);
        connection.Database = "maple2_validation_resets_" + Guid.NewGuid().ToString("N")[..12];
        options = new DbContextOptionsBuilder().UseMySql(connection.ConnectionString, version,
            mysql => mysql.MigrationsAssembly(typeof(GuildQuestRewardsMigration).Assembly.GetName().Name)).Options;
        using (var context = new Ms2Context(options)) {
            if (context.Database.CanConnect()) {
                throw new InvalidOperationException("Refusing to reuse an existing reset-test database.");
            }
            databaseCreated = true;
            context.Database.Migrate();
        }
        items = new ItemMetadataStorage(metadata);
        tables = new TableMetadataStorage(metadata);
        serverTables = new ServerTableMetadataStorage(metadata);
        storage = new GameStorage(options, items, new MapMetadataStorage(metadata),
            new AchievementMetadataStorage(metadata), new QuestMetadataStorage(metadata), tables, serverTables,
            NullLogger<GameStorage>.Instance, new FunctionCubeMetadataStorage(metadata));
    }

    [OneTimeTearDown]
    public void RemoveIsolatedDatabase() {
        if (databaseCreated) {
            using var context = new Ms2Context(options);
            context.Database.EnsureDeleted();
        }
        metadata?.Dispose();
    }

    [TestCase(ResetType.Day)]
    [TestCase(ResetType.Week)]
    [TestCase(ResetType.Month)]
    public void OnlineResetCommitsItsTokenAndSurvivesTheNextStrictSave(ResetType type) {
        using SessionData data = NewSession();
        GameSession session = data.Session;
        Account account = session.Player.Value.Account;
        DateTime originalVersion = account.LastModified;

        RunGlobalReset(type);
        using (GameStorage.Request check = storage.Context()) {
            Account stored = check.GetAccount(account.Id)!;
            Assert.That(stored.LastModified, Is.EqualTo(originalVersion), "Bulk resets must not touch online account versions.");
            Assert.That(stored.PremiumRewardsClaimed, Is.EqualTo(new[] { 7, 8 }));
            Assert.That(stored.PrestigeRewardsClaimed, Is.EqualTo(new[] { 9 }));
        }

        Assert.That(RunOnlineReset(session, type), Is.True);
        Assert.That(session.PersistenceAborted, Is.False);
        Assert.That(account.LastModified, Is.GreaterThan(originalVersion));
        Assert.That(data.World.Calls, Is.EqualTo(2));
        Assert.That(data.World.ItemAndSaveHeld, Is.True);
        Assert.That(data.World.CorrectLeaseReleased, Is.True);
        AssertResetState(account, type);

        Assert.That(session.SessionSave(), Is.True, "A routine reset must not poison the next account CAS save.");
        Assert.That(session.PersistenceAborted, Is.False);
        using GameStorage.Request verify = storage.Context();
        Account persisted = verify.GetAccount(account.Id)!;
        AssertResetState(persisted, type);
        Assert.That(persisted.LastModified, Is.EqualTo(account.LastModified));
        Assert.That(persisted.MaxCharacters, Is.EqualTo(17));
        Assert.That(persisted.PremiumTime, Is.EqualTo(2345678901));
        Assert.That(persisted.SurvivalExp, Is.EqualTo(456));
        Assert.That(verify.LoadPlayer(account.Id, session.CharacterId, 1, 1)!.Currency.Meso, Is.EqualTo(12345));
    }

    [TestCase(ResetType.Day)]
    [TestCase(ResetType.Week)]
    [TestCase(ResetType.Month)]
    public void OfflineAccountsStillResetWithoutChangingUnrelatedFields(ResetType type) {
        Player player = CreatePlayer();
        player.Account.Online = false;
        player.Character.Channel = -1;
        using (GameStorage.Request seed = storage.Context()) {
            Assert.That(seed.SavePlayer(player), Is.True);
        }
        DateTime before = player.Account.LastModified;

        RunGlobalReset(type);
        using GameStorage.Request verify = storage.Context();
        Account account = verify.GetAccount(player.Account.Id)!;
        AssertResetState(account, type);
        Assert.That(account.LastModified, Is.GreaterThan(before));
        Assert.That(account.MaxCharacters, Is.EqualTo(17));
        Assert.That(account.PremiumTime, Is.EqualTo(2345678901));
        Assert.That(account.SurvivalExp, Is.EqualTo(456));
    }

    [Test]
    public void ResetCannotRefreshAStaleTokenOverAnUnmergedExternalChange() {
        using SessionData data = NewSession();
        long id = data.Session.AccountId;
        using (var context = new Ms2Context(options)) {
            context.Database.ExecuteSqlInterpolated($"""
                UPDATE `account` SET `MaxCharacters` = 19,
                    `LastModified` = GREATEST(CURRENT_TIMESTAMP(6), DATE_ADD(`LastModified`, INTERVAL 1 MICROSECOND))
                WHERE `Id` = {id}
                """);
        }
        Assert.That(data.Session.DailyReset(), Is.False);
        Assert.That(data.Session.PersistenceAborted, Is.True);
        using GameStorage.Request verify = storage.Context();
        Account account = verify.GetAccount(id)!;
        Assert.That(account.MaxCharacters, Is.EqualTo(19));
        Assert.That(account.PremiumRewardsClaimed, Is.EqualTo(new[] { 7, 8 }));
    }

    [TestCase(ResetType.Day)]
    [TestCase(ResetType.Week)]
    [TestCase(ResetType.Month)]
    public async Task LoginLocksItsSnapshotAgainstBulkResetAndDrainsTheDeferredReset(ResetType type) {
        Player previous = CreatePlayer();
        previous.Account.Online = false;
        previous.Character.Channel = -1;
        using (GameStorage.Request seed = storage.Context()) {
            Assert.That(seed.SavePlayer(previous), Is.True);
        }

        using var read = new PausedAccountRead();
        using var resetStarted = new ManualResetEventSlim();
        DbContextOptions loadingOptions = new DbContextOptionsBuilder(options).AddInterceptors(read).Options;
        DbContextOptions resetOptions = new DbContextOptionsBuilder(options).AddInterceptors(new ResetAttempt(resetStarted)).Options;
        Task<Player?> load = Task.Run(() => {
            using var context = new Ms2Context(loadingOptions);
            using var request = new GameStorage.Request(storage, context, NullLogger<GameStorage>.Instance);
            request.BeginTransaction();
            Player? player = request.LoadPlayer(previous.Account.Id, previous.Character.Id, 1, 1);
            Assert.That(request.Commit(), Is.True);
            return player;
        });
        Task reset = Task.CompletedTask;
        bool blocked = false;
        try {
            Assert.That(read.Entered.Wait(TimeSpan.FromSeconds(10)), Is.True);
            reset = Task.Run(() => {
                using var context = new Ms2Context(resetOptions);
                using var request = new GameStorage.Request(storage, context, NullLogger<GameStorage>.Instance);
                RunGlobalReset(request, type);
            });
            Assert.That(resetStarted.Wait(TimeSpan.FromSeconds(10)), Is.True);
            blocked = !reset.Wait(TimeSpan.FromMilliseconds(250));
        } finally {
            read.Continue.Set();
            await Task.WhenAll(load, reset).WaitAsync(TimeSpan.FromSeconds(30));
        }
        Assert.That(blocked, Is.True, "An offline reset must not invalidate the account snapshot while login is claiming it.");
        Player loaded = (await load)!;
        Assert.That(loaded.Account.PremiumRewardsClaimed, Is.EqualTo(new[] { 7, 8 }));
        Assert.That(loaded.Account.LastModified, Is.GreaterThan(previous.Account.LastModified));

        using SessionData data = NewSession(loaded, initializeResets: false);
        Assert.That(RunOnlineReset(data.Session, type), Is.False);
        Assert.That(RunOnlineReset(data.Session, type), Is.False);
        Assert.That(data.World.Calls, Is.Zero);
        Assert.That(CompleteResetInitialization(data.Session), Is.True);
        Assert.That(data.World.Calls, Is.EqualTo(2), "Repeated resets queued during loading must be applied only once.");
        Assert.That(CompleteResetInitialization(data.Session), Is.True);
        Assert.That(data.World.Calls, Is.EqualTo(2));
        AssertResetState(loaded.Account, type);
        Assert.That(data.Session.SessionSave(), Is.True);
        Assert.That(data.Session.PersistenceAborted, Is.False);
        using GameStorage.Request verify = storage.Context();
        Account stored = verify.GetAccount(loaded.Account.Id)!;
        AssertResetState(stored, type);
        Assert.That(stored.MaxCharacters, Is.EqualTo(17));
        Assert.That(stored.LastModified, Is.EqualTo(loaded.Account.LastModified));
    }

    [TestCase(ResetType.Day)]
    [TestCase(ResetType.Week)]
    [TestCase(ResetType.Month)]
    public void LoginAfterOfflineResetLoadsTheWholeResetSnapshot(ResetType type) {
        Player previous = CreatePlayer();
        previous.Account.Online = false;
        previous.Character.Channel = -1;
        using (GameStorage.Request seed = storage.Context()) {
            Assert.That(seed.SavePlayer(previous), Is.True);
        }
        RunGlobalReset(type);
        Player loaded;
        using (GameStorage.Request request = storage.Context()) {
            loaded = request.LoadPlayer(previous.Account.Id, previous.Character.Id, 1, 1)!;
        }
        AssertResetState(loaded.Account, type);
        using GameStorage.Request save = storage.Context();
        Assert.That(save.SavePlayer(loaded), Is.True);
        Assert.That(loaded.Account.MaxCharacters, Is.EqualTo(17));
        Assert.That(loaded.Account.PremiumTime, Is.EqualTo(2345678901));
        Assert.That(loaded.Currency.Meso, Is.EqualTo(12345));
    }

    [TestCase(ResetType.Day)]
    [TestCase(ResetType.Week)]
    public void BulkResetPreservesLiveCountersButResetsOfflineCharactersAndAccounts(ResetType type) {
        Player online = CreatePlayer();
        Player offline = CreatePlayer();
        offline.Account.Online = false;
        offline.Character.Channel = -1;
        Character otherCharacter;
        using (GameStorage.Request seed = storage.Context()) {
            Assert.That(seed.SavePlayer(offline), Is.True);
            otherCharacter = new Character {
                AccountId = online.Account.Id,
                Name = "Other" + Guid.NewGuid().ToString("N")[..7],
                MapId = 2000001,
                Job = Job.Newbie,
                Mastery = new Mastery(),
            };
            otherCharacter.ReturnMaps.Push(otherCharacter.MapId);
            otherCharacter = seed.CreateCharacter(otherCharacter)!;
            Assert.That(seed.InitNewCharacter(otherCharacter.Id, new Unlock()), Is.True);
        }
        Assert.That(items.TryGet(20000027, out ItemMetadata? itemMetadata), Is.True);
        (long Owner, bool AccountWide, bool Active)[] scopes = [
            (online.Account.Id, true, true), (online.Character.Id, false, true),
            (offline.Account.Id, true, false), (offline.Character.Id, false, false),
            (otherCharacter.Id, false, false),
        ];
        const int shopId = 987654;
        foreach ((long owner, bool accountWide, _) in scopes) {
            using GameStorage.Request seed = storage.Context();
            Assert.That(seed.CreateCharacterShopData(owner, new CharacterShopData {
                ShopId = shopId,
                Interval = type,
                RestockCount = 4,
            }), Is.Not.Null);
            Assert.That(seed.CreateCharacterShopItemData(owner, new CharacterShopItemData {
                ShopId = shopId,
                ShopItemId = 1,
                StockPurchased = 7,
                Item = new Item(itemMetadata!),
            }), Is.Not.Null);
            Assert.That(seed.CreateDungeonRecord(new DungeonRecord(shopId, accountWide) {
                UnionSubClears = 3,
                ExtraSubClears = 2,
                UnionClears = 5,
                ExtraClears = 1,
                UnionCooldownTimestamp = DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds(),
                UnionSubCooldownTimestamp = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds(),
            }, owner, accountWide), Is.Not.Null);
        }
        RunGlobalReset(type);
        foreach ((long owner, bool accountWide, bool active) in scopes) {
            using GameStorage.Request verify = storage.Context();
            Assert.That(verify.GetCharacterShopData(owner, shopId)!.RestockCount, Is.EqualTo(active ? 4 : 0));
            Assert.That(verify.GetCharacterShopItemData(owner).Single(item => item.ShopId == shopId).StockPurchased,
                Is.EqualTo(active ? 7 : 0));
            DungeonRecord record = verify.GetDungeonRecords(owner, accountWide)[shopId];
            Assert.That(record.UnionSubClears, Is.EqualTo(!active && type == ResetType.Day ? 0 : 3));
            Assert.That(record.UnionClears, Is.EqualTo(!active && type == ResetType.Week ? 0 : 5));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void QueuedProgressionIsDurableBeforeImmediateDisconnectOrMigrationHandoff(bool migrating) {
        using SessionData data = NewSession();
        GameSession session = data.Session;
        int applied = 0;
        long originalExp = session.Player.Value.Character.Exp;
        int originalProgress = session.Config.ExplorationProgress;
        lock (session.Item) {
            session.Item.AfterUnlock(() => {
                Assert.That(Monitor.IsEntered(session.Item), Is.False);
                Assert.That(Monitor.IsEntered(typeof(GameSession).GetField("saveSync", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!), Is.False);
                Assert.That(data.World.Calls, Is.Zero);
                session.Player.Value.Character.Exp += 17;
                session.Config.ExplorationProgress++;
                applied++;
                session.Item.AfterUnlock(() => {
                    session.Player.Value.Currency.Meso += 13;
                    applied++;
                });
            });
        }
        session.State = SessionState.ChangeMap;
        bool handoffChecked = false;
        if (migrating) {
            // The real handoff path runs, but synthetic transport teardown must not open sockets or servers.
            Set<GameSession>(session, "gameDisposeState", 2);
            data.World.BeforeMigration = () => {
                Assert.That(Monitor.IsEntered(session.Item), Is.False);
                VerifyProgress();
                handoffChecked = true;
            };
            session.MigrateToPlanner(PlotMode.Normal);
            Assert.That(handoffChecked, Is.True);
        } else {
            var server = (GameServer) RuntimeHelpers.GetUninitializedObject(typeof(GameServer));
            GC.SuppressFinalize(server);
            Set<GameServer>(server, "mutex", new object());
            Set<GameServer>(server, "connectingSessions", new HashSet<GameSession>());
            Set<GameServer>(server, "sessions", new ConcurrentDictionary<long, GameSession>());
            Set<GameSession>(session, "server", server);
            Set<GameSession>(session, "ItemLockStaging", new long[18]);
            Set<GameSession>(session, "DismantleStaging", new (long, int)[100]);
            session.GroupChats = new ConcurrentDictionary<int, GroupChatManager>();
            session.Clubs = new ConcurrentDictionary<long, ClubManager>();
            session.Buffs = new BuffManager(session.Player);
            session.Disconnect();
            Assert.That(session.State, Is.EqualTo(SessionState.Disconnected));
            Assert.That(session.Scheduler.Running, Is.False);
        }
        Assert.That(session.PersistenceAborted, Is.False);
        Assert.That(applied, Is.EqualTo(2));
        Assert.That(session.Scheduler.Queued, Is.Zero);
        VerifyProgress();

        void VerifyProgress() {
            using GameStorage.Request verify = storage.Context();
            Assert.That(verify.GetCharacter(session.CharacterId)!.Exp, Is.EqualTo(originalExp + 17));
            Assert.That(verify.LoadCharacterConfig(session.CharacterId).ExplorationProgress, Is.EqualTo(originalProgress + 1));
            using var context = new Ms2Context(options);
            context.Database.OpenConnection();
            using DbCommand command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT JSON_UNQUOTE(JSON_EXTRACT(`Currency`, '$.Meso')) FROM `character` WHERE `Id` = @id";
            DbParameter id = command.CreateParameter();
            id.ParameterName = "@id";
            id.Value = session.CharacterId;
            command.Parameters.Add(id);
            Assert.That(Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture), Is.EqualTo(12358));
        }
    }

    private void RunGlobalReset(ResetType type) {
        using GameStorage.Request request = storage.Context();
        RunGlobalReset(request, type);
    }

    private static void RunGlobalReset(GameStorage.Request request, ResetType type) {
        switch (type) {
            case ResetType.Day: request.DailyReset(); break;
            case ResetType.Week: request.WeeklyReset(); break;
            case ResetType.Month: request.MonthlyReset(); break;
            default: throw new ArgumentOutOfRangeException(nameof(type));
        }
    }

    private static bool RunOnlineReset(GameSession session, ResetType type) {
        return type switch {
            ResetType.Day => session.DailyReset(),
            ResetType.Week => session.WeeklyReset(),
            ResetType.Month => session.MonthlyReset(),
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
    }

    private static void AssertResetState(Account account, ResetType type) {
        Assert.That(account.MesoMarketListed, Is.EqualTo(type == ResetType.Day ? 0 : 3));
        Assert.That(account.MesoMarketPurchased, Is.EqualTo(type == ResetType.Month ? 0 : 4));
        Assert.That(account.PremiumRewardsClaimed, Is.EqualTo(type == ResetType.Day ? Array.Empty<int>() : new[] { 7, 8 }));
        Assert.That(account.PrestigeRewardsClaimed, Is.EqualTo(type == ResetType.Week ? Array.Empty<int>() : new[] { 9 }));
        Assert.That(account.PrestigeExp, Is.EqualTo(type == ResetType.Day ? 1000 : 100));
        Assert.That(account.PrestigeLevelsGained, Is.EqualTo(type == ResetType.Day ? 0 : 2));
    }

    private Player CreatePlayer() {
        Account account;
        Character character;
        using (GameStorage.Request request = storage.Context()) {
            account = request.CreateAccount(new Account {
                Username = "reset" + Guid.NewGuid().ToString("N")[..12],
            }, Guid.NewGuid().ToString("N"));
            character = new Character {
                AccountId = account.Id,
                Name = "R" + Guid.NewGuid().ToString("N")[..10],
                MapId = 2000001,
                Job = Job.Newbie,
                Mastery = new Mastery(),
            };
            character.ReturnMaps.Push(character.MapId);
            character = request.CreateCharacter(character)!;
            Assert.That(request.InitNewCharacter(character.Id, new Unlock()), Is.True);
        }
        Player player;
        using (GameStorage.Request request = storage.Context()) {
            player = request.LoadPlayer(account.Id, character.Id, 1, 1)!;
        }
        player.Account.MaxCharacters = 17;
        player.Account.PremiumTime = 2345678901;
        player.Account.SurvivalExp = 456;
        player.Account.PremiumRewardsClaimed = new List<int> { 7, 8 };
        player.Account.PrestigeRewardsClaimed = new List<int> { 9 };
        player.Account.PrestigeExp = 100;
        player.Account.PrestigeCurrentExp = 1000;
        player.Account.PrestigeLevelsGained = 2;
        player.Account.MesoMarketListed = 3;
        player.Account.MesoMarketPurchased = 4;
        player.Currency.Meso = 12345;
        using GameStorage.Request save = storage.Context();
        Assert.That(save.SavePlayer(player), Is.True);
        return player;
    }

    private SessionData NewSession(Player? player = null, bool initializeResets = true) {
        player ??= CreatePlayer();
        var session = (GameSession) RuntimeHelpers.GetUninitializedObject(typeof(GameSession));
        GC.SuppressFinalize(session);
        var packets = new BlockingCollection<(byte[], int)>();
        Set<NetworkSession>(session, "Logger", Log.Logger);
        Set<NetworkSession>(session, "sendQueue", packets);
        Set<NetworkSession>(session, "lastSentPackets", new ConcurrentDictionary<SendOp, byte[]>());
        Set<NetworkSession>(session, "<AccountId>k__BackingField", player.Account.Id);
        Set<NetworkSession>(session, "<CharacterId>k__BackingField", player.Character.Id);
        Set<GameSession>(session, "saveSync", new object());
        Set<GameSession>(session, "resetSync", new object());
        var scheduler = new EventQueue(Log.Logger);
        scheduler.Start();
        Set<GameSession>(session, "Scheduler", scheduler);
        Set<GameSession>(session, "<GameStorage>k__BackingField", storage);
        Set<GameSession>(session, "<ItemMetadata>k__BackingField", items);
        Set<GameSession>(session, "<TableMetadata>k__BackingField", tables);
        Set<GameSession>(session, "<ServerTableMetadata>k__BackingField", serverTables);
        Set<GameSession>(session, "<SkillMetadata>k__BackingField", new SkillMetadataStorage(metadata));
        Set<GameSession>(session, "<AchievementMetadata>k__BackingField", new AchievementMetadataStorage(metadata));
        Set<GameSession>(session, "<QuestMetadata>k__BackingField", new QuestMetadataStorage(metadata));
        var fieldPlayer = (FieldPlayer) RuntimeHelpers.GetUninitializedObject(typeof(FieldPlayer));
        GC.SuppressFinalize(fieldPlayer);
        Set<Actor<Player>>(fieldPlayer, "<Value>k__BackingField", player);
        Set<Actor<Player>>(fieldPlayer, "<ObjectId>k__BackingField", player.ObjectId);
        Set<FieldPlayer>(fieldPlayer, "Session", session);
        Set<GameSession>(session, "<Player>k__BackingField", fieldPlayer);
        using GameStorage.Request db = storage.Context();
        session.Currency = new CurrencyManager(session);
        session.Config = new ConfigManager(db, session);
        session.Item = new ItemManager(db, session, null!);
        session.Shop = new ShopManager(session);
        session.UgcMarket = new UgcMarketManager(session);
        session.Housing = new HousingManager(session, tables);
        session.Survival = new SurvivalManager(session);
        session.Achievement = new AchievementManager(session);
        session.Quest = new QuestManager(session);
        session.GameEvent = (GameEventManager) RuntimeHelpers.GetUninitializedObject(typeof(GameEventManager));
        Set<GameEventManager>(session.GameEvent, "session", session);
        Set<GameEventManager>(session.GameEvent, "eventValues", new Dictionary<int, Dictionary<GameEventUserValueType, GameEventUserValue>>());
        session.Dungeon = (DungeonManager) RuntimeHelpers.GetUninitializedObject(typeof(DungeonManager));
        Set<DungeonManager>(session.Dungeon, "session", session);
        Set<DungeonManager>(session.Dungeon, "<CharacterRecords>k__BackingField", new Dictionary<int, DungeonRecord>());
        Set<DungeonManager>(session.Dungeon, "<AccountRecords>k__BackingField", new Dictionary<int, DungeonRecord>());
        session.Dungeon.Records = new Dictionary<int, DungeonRecord>();
        var world = new LeaseClient(session);
        Set<GameSession>(session, "<World>k__BackingField", world);
        if (initializeResets) {
            Assert.That(CompleteResetInitialization(session), Is.True);
        }
        return new SessionData(session, packets, world);
    }

    private static bool CompleteResetInitialization(GameSession session) {
        return (bool) typeof(GameSession).GetMethod("CompleteResetInitialization", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session, null)!;
    }

    private static void Set<T>(object target, string name, object value) {
        typeof(T).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.SetValue(target, value);
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"{name} is required.");

    private sealed record SessionData(GameSession Session, BlockingCollection<(byte[], int)> Packets, LeaseClient World) : IDisposable {
        public void Dispose() => Packets.Dispose();
    }

    private sealed class PausedAccountRead : DbCommandInterceptor, IDisposable {
        public readonly ManualResetEventSlim Entered = new();
        public readonly ManualResetEventSlim Continue = new();
        private int paused;

        public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result) {
            if (command.CommandText.Contains("FROM `account`", StringComparison.Ordinal) &&
                Interlocked.Exchange(ref paused, 1) == 0) {
                Entered.Set();
                if (!Continue.Wait(TimeSpan.FromSeconds(15))) {
                    throw new TimeoutException("The login account-read gate was not released.");
                }
            }
            return result;
        }

        public void Dispose() {
            Entered.Dispose();
            Continue.Dispose();
        }
    }

    private sealed class ResetAttempt(ManualResetEventSlim started) : DbCommandInterceptor {
        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result) {
            if (command.CommandText.Contains("UPDATE `account`", StringComparison.Ordinal)) {
                started.Set();
            }
            return result;
        }
    }

    private sealed class LeaseClient(GameSession session) : WorldClient {
        private LockRequest? acquired;
        public int Calls;
        public bool ItemAndSaveHeld = true;
        public bool CorrectLeaseReleased;
        public Action? BeforeMigration;

        public override LockResponse AcquireLock(LockRequest request, CallOptions options) {
            ObserveLocks();
            acquired = request.Clone();
            return new LockResponse { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeMilliseconds() };
        }

        public override LockResponse ReleaseLock(LockRequest request, CallOptions options) {
            ObserveLocks();
            CorrectLeaseReleased = request.Equals(acquired);
            return new LockResponse();
        }

        public override MigrateOutResponse MigrateOut(MigrateOutRequest request, CallOptions options) {
            BeforeMigration?.Invoke();
            return new MigrateOutResponse { IpAddress = "127.0.0.1", Port = 20003, Token = 1 };
        }

        public override PlayerConfigResponse PlayerConfig(PlayerConfigRequest request, CallOptions options) {
            return new PlayerConfigResponse();
        }

        private void ObserveLocks() {
            Calls++;
            object saveSync = typeof(GameSession).GetField("saveSync", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
            ItemAndSaveHeld &= Monitor.IsEntered(session.Item) && Monitor.IsEntered(saveSync);
        }
    }
}
