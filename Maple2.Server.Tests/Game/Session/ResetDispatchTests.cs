using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Grpc.Core;
using Maple2.Model.Enum;
using Maple2.Server.Game;
using Maple2.Server.Game.Service;
using Maple2.Server.Game.Session;
using Maple2.Server.World;
using Maple2.Server.World.Service;
using Serilog;
using NetworkSession = Maple2.Server.Core.Network.Session;

namespace Maple2.Server.Tests.Game.Session;

public class ResetDispatchTests {
    [TestCase(ResetType.Day, false)]
    [TestCase(ResetType.Week, false)]
    [TestCase(ResetType.Month, false)]
    [TestCase(ResetType.Day, true)]
    [TestCase(ResetType.Week, true)]
    [TestCase(ResetType.Month, true)]
    public async Task ChannelResetReportsIncompleteSessionFailureInsteadOfSuccess(ResetType type, bool incompleteSession) {
        var server = (GameServer) RuntimeHelpers.GetUninitializedObject(typeof(GameServer));
        GC.SuppressFinalize(server);
        var sessions = new ConcurrentDictionary<long, GameSession>();
        typeof(GameServer).GetField("sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(server, sessions);
        typeof(GameServer).GetField("mutex", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(server, new object());
        typeof(GameServer).GetField("connectingSessions", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(server, new HashSet<GameSession>());
        if (incompleteSession) {
            var session = (GameSession) RuntimeHelpers.GetUninitializedObject(typeof(GameSession));
            GC.SuppressFinalize(session);
            typeof(NetworkSession).GetField("Logger", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, Log.Logger);
            typeof(NetworkSession).GetField("<AccountId>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, 123L);
            typeof(GameSession).GetField("resetSync", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, new object());
            sessions[1] = session;
        }
        var service = new ChannelService(server, null!, null!, null!, null!, null!);
        GameResetRequest request = type switch {
            ResetType.Day => new GameResetRequest { Daily = new GameResetRequest.Types.Daily() },
            ResetType.Week => new GameResetRequest { Weekly = new GameResetRequest.Types.Weekly() },
            ResetType.Month => new GameResetRequest { Monthly = new GameResetRequest.Types.Monthly() },
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
        GameResetResponse response = await service.GameReset(request, null!);
        Assert.That(response.Error, Is.EqualTo(incompleteSession ? 1 : 0));
    }

    [TestCase(ResetType.Day)]
    [TestCase(ResetType.Week)]
    [TestCase(ResetType.Month)]
    public void ConnectingSessionKeepsResetUntilInitializationCompletes(ResetType type) {
        var server = (GameServer) RuntimeHelpers.GetUninitializedObject(typeof(GameServer));
        GC.SuppressFinalize(server);
        var session = (GameSession) RuntimeHelpers.GetUninitializedObject(typeof(GameSession));
        GC.SuppressFinalize(session);
        typeof(NetworkSession).GetField("Logger", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, Log.Logger);
        typeof(NetworkSession).GetField("<AccountId>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, 123L);
        typeof(GameSession).GetField("resetSync", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, new object());
        typeof(GameServer).GetField("mutex", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(server, new object());
        typeof(GameServer).GetField("sessions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(server, new ConcurrentDictionary<long, GameSession>());
        typeof(GameServer).GetField("connectingSessions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(server, new HashSet<GameSession> { session });

        bool completed = type switch {
            ResetType.Day => server.DailyReset(),
            ResetType.Week => server.WeeklyReset(),
            ResetType.Month => server.MonthlyReset(),
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
        Assert.That(completed, Is.False, "A queued reset is not yet a successful persistence result.");
        Assert.That(typeof(GameSession).GetField("pendingResetPeriods", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session),
            Is.EqualTo(1 << (int) type));
    }

    [Test]
    public void MissingResetPeriodIsRejectedByWorldAndChannelRpc() {
        var channel = new ChannelService(null!, null!, null!, null!, null!, null!);
        RpcException? channelError = Assert.ThrowsAsync<RpcException>(async () => await channel.GameReset(new GameResetRequest(), null!));
        Assert.That(channelError!.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));

        var worldServer = (WorldServer) RuntimeHelpers.GetUninitializedObject(typeof(WorldServer));
        var world = (WorldService) RuntimeHelpers.GetUninitializedObject(typeof(WorldService));
        typeof(WorldService).GetField("worldServer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(world, worldServer);
        RpcException? worldError = Assert.ThrowsAsync<RpcException>(async () => await world.GameReset(new GameResetRequest(), null!));
        Assert.That(worldError!.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
        MethodInfo rpc = typeof(WorldService).GetMethod(nameof(WorldService.GameReset), [typeof(GameResetRequest), typeof(ServerCallContext)])!;
        Assert.That(rpc.GetBaseDefinition().DeclaringType, Is.EqualTo(typeof(Maple2.Server.World.Service.World.WorldBase)));
    }
}
