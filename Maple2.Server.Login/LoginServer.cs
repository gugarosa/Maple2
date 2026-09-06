using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Maple2.Database.Storage;
using Maple2.Model.Game;
using Maple2.Model.Game.Event;
using Maple2.Server.Core.Constants;
using Maple2.Server.Core.Network;
using Maple2.Server.Core.Packets;
using Maple2.Server.Login.Session;

namespace Maple2.Server.Login;

public class LoginServer : Server<LoginSession> {
    private readonly object mutex = new();
    private readonly HashSet<LoginSession> connectingSessions;
    private readonly ConcurrentDictionary<long, LoginSession> sessions;
    private readonly IList<SystemBanner> bannerCache;
    private readonly GameStorage gameStorage;

    public LoginServer(PacketRouter<LoginSession> router, IComponentContext context, GameStorage gameStorage, ServerTableMetadataStorage serverTableMetadataStorage)
            : base(Target.LoginPort, router, context, serverTableMetadataStorage) {
        connectingSessions = [];
        sessions = new ConcurrentDictionary<long, LoginSession>();

        this.gameStorage = gameStorage;
        using GameStorage.Request db = this.gameStorage.Context();
        bannerCache = db.GetBanners();
    }

    public override void OnConnected(LoginSession session) {
        lock (mutex) {
            connectingSessions.Remove(session);
            sessions[session.AccountId] = session;
        }
    }

    public override void OnDisconnected(LoginSession session) {
        lock (mutex) {
            connectingSessions.Remove(session);
        }
        sessions.TryRemove(KeyValuePair.Create(session.AccountId, session));
    }

    public bool GetSession(long accountId, [NotNullWhen(true)] out LoginSession? session) {
        return sessions.TryGetValue(accountId, out session);
    }

    protected override void AddSession(LoginSession session) {
        lock (mutex) {
            connectingSessions.Add(session);
        }

        Logger.Information("Login client connected: {Session}", session);
        session.Start();
    }

    public IList<SystemBanner> GetSystemBanners() => bannerCache;

    public IEnumerable<GameEvent> GetEvents() => eventCache.Values.Where(gameEvent => gameEvent.IsActive());

    public override async Task StopAsync(CancellationToken cancellationToken) {
        await base.StopAsync(cancellationToken);
        LoginSession[] connecting;
        lock (mutex) {
            connecting = connectingSessions.ToArray();
        }
        foreach (LoginSession session in connecting.Concat(sessions.Values).Distinct()) {
            session.Send(NoticePacket.Disconnect(new InterfaceText("LoginServer Maintenance")));
            session.Disconnect();
        }
    }

    public List<LoginSession> GetSessions() => sessions.Values.ToList();
}
