using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Grpc.Core;
using Maple2.Database.Storage;
using Maple2.Model.Enum;
using Maple2.Model.Game;
using Maple2.Model.Metadata;
using Maple2.Server.Core.Constants;
using Maple2.Server.Core.Network;
using Maple2.Server.Core.Packets;
using Maple2.Server.World.Service;
using static Maple2.Model.Error.CharacterCreateError;
using WorldClient = Maple2.Server.World.Service.World.WorldClient;

namespace Maple2.Server.Login.Session;

public class LoginSession : Core.Network.Session {
    protected override PatchType Type => PatchType.Delete;

    private bool disposed;
    private static readonly TimeSpan LockRpcTimeout = TimeSpan.FromSeconds(2);
    public readonly LoginServer Server;

    public new long AccountId { get; private set; }
    public new long CharacterId { get; private set; } // Used only as a temporary variable
    public Guid MachineId { get; private set; }

    #region Autofac Autowired
    // ReSharper disable MemberCanBePrivate.Global
    public required WorldClient World { private get; init; }
    public required GameStorage GameStorage { private get; init; }
    // ReSharper restore All
    #endregion

    private Account account = null!;

    public int ServerTick;
    public int ClientTick;

    public LoginSession(TcpClient tcpClient, LoginServer server) : base(tcpClient) {
        Server = server;
        State = SessionState.ChangeMap;
    }

    public void Init(long accountId, Guid machineId) {
        AccountId = accountId;
        MachineId = machineId;

        State = SessionState.Connected;
        Server.OnConnected(this);
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
        throw new RpcException(new Status(StatusCode.Unavailable, "Account data is busy. Please try again."));
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

    public void ListServers() {
        ChannelsResponse response = World.Channels(new ChannelsRequest());
        Send(BannerListPacket.Load(Server.GetSystemBanners()));
        Send(ServerListPacket.Load(Target.SERVER_NAME, [new IPEndPoint(Target.LoginIp, Target.LoginPort)], response.Channels));
    }

    public void ListCharacters() {
        (LockRequest request, long expiresAt) = AcquireLock(AccountId);
        try {
            using GameStorage.Request db = GameStorage.Context();
            (Account? readAccount, IList<Character>? characters) = db.ListCharacters(AccountId);
            if (readAccount == null || characters == null) {
                Logger.Error("Failed to load characters for account: {AccountId}", AccountId);
                throw new RpcException(new Status(StatusCode.Internal, "Character data could not be loaded."));
            }

            var entries = new List<(Character, IDictionary<ItemGroup, List<Item>>)>();
            foreach (Character character in characters) {
                IDictionary<ItemGroup, List<Item>> equips =
                    db.GetItemGroups(character.Id, ItemGroup.Gear, ItemGroup.Outfit, ItemGroup.Badge);
                entries.Add((character, equips));
            }
            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= expiresAt) {
                throw new RpcException(new Status(StatusCode.DeadlineExceeded, "Account data lease expired."));
            }

            account = readAccount;
            Send(CharacterListPacket.SetMax(account.MaxCharacters, Constant.ServerMaxCharacters));
            Send(CharacterListPacket.StartList());
            Send(CharacterListPacket.AddEntries(account, entries));
            Send(CharacterListPacket.EndList());
        } catch (Exception ex) when (ex is not RpcException) {
            Logger.Error(ex, "Failed to load characters for account {AccountId}", AccountId);
            throw new RpcException(new Status(StatusCode.Internal, "Character data could not be loaded."));
        } finally {
            ReleaseLock(request);
        }
    }

    public void CreateCharacter(Character createCharacter, List<Item> createOutfits) {
        using GameStorage.Request db = GameStorage.Context();
        db.BeginTransaction();
        Character? character = db.CreateCharacter(createCharacter);
        if (character == null) {
            Logger.Error("Failed to create character: {CharacterId}", createCharacter.Id);
            Send(CharacterListPacket.CreateError(s_char_err_system));
            return;
        }
        var unlock = new Unlock();

        foreach (int emoteId in Constant.DefaultEmotes) {
            unlock.Emotes.Add(emoteId);
        }
        character.AchievementInfo = db.GetAchievementInfo(AccountId, character.Id);
        character.PremiumTime = account.PremiumTime;

        if (!db.InitNewCharacter(character.Id, unlock)) {
            Logger.Error("Failed to initialize character: {CharacterId}", character.Id);
            Send(CharacterListPacket.CreateError(s_char_err_system));
            return;
        }

        foreach (Item item in createOutfits) {
            item.Transfer?.Bind(character);
        }
        List<Item>? outfits = db.CreateItems(character.Id, createOutfits.ToArray());

        if (outfits == null || !db.Commit()) {
            Send(CharacterListPacket.CreateError(s_char_err_system));
            return;
        }
        CharacterId = character.Id;

        Send(CharacterListPacket.SetMax(account.MaxCharacters, Constant.ServerMaxCharacters));
        Send(CharacterListPacket.AppendEntry(account, character,
            new Dictionary<ItemGroup, List<Item>> {
                { ItemGroup.Outfit, outfits },
            }));
    }

    #region Dispose
    ~LoginSession() => Dispose(false);

    protected override void Dispose(bool disposing) {
        if (disposed) return;
        disposed = true;

        try {
            Server.OnDisconnected(this);
        } catch (Exception ex) {
            Logger.Debug(ex, "Error during LoginSession.OnDisconnected");
        }

        State = SessionState.Disconnected;

        try {
            base.Dispose(disposing);
        } catch (Exception ex) {
            Logger.Debug(ex, "Error during LoginSession base disposal");
        }
    }
    #endregion
}
