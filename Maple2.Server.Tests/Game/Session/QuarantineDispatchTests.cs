using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Maple2.Model.Enum;
using Maple2.Model.Game;
using Maple2.PacketLib.Tools;
using Maple2.Server.Core.Constants;
using Maple2.Server.Core.Network;
using Maple2.Server.Core.PacketHandlers;
using Maple2.Server.Game.Manager.Field;
using Maple2.Server.Game.Manager.Items;
using Maple2.Server.Game.Commands;
using Maple2.Server.Game.Model;
using Maple2.Server.Game.PacketHandlers.Field;
using Maple2.Server.Game.Session;
using Maple2.Tools.Scheduler;
using Serilog;
using NetworkSession = Maple2.Server.Core.Network.Session;

namespace Maple2.Server.Tests.Game.Session;

public class QuarantineDispatchTests {
    [Test]
    public void QuarantineBlocksImmediateDispatchAndConditionCallbacks() {
        (GameSession session, var packets) = CreateSession();
        var handler = new ImmediateHandler();
        var router = new PacketRouter<GameSession>([handler]);
        using (packets) {
            session.AbortPersistence("Injected uncertain commit.");
            router.OnPacket(session, new ByteReader([(byte) RecvOp.Quest, 0], 0));
            session.ConditionUpdate(ConditionType.item_collect);
            Assert.That(handler.Calls, Is.Zero);
            Assert.That(session.CanProcessPackets, Is.False);
        }
    }

    [Test]
    public void AlreadyQueuedPacketsAreDiscardedAfterQuarantine() {
        (GameSession session, var sent) = CreateSession();
        var field = (FieldManager) RuntimeHelpers.GetUninitializedObject(typeof(FieldManager));
        var packets = new List<(FieldPacketHandler, GameSession, ByteReader)>();
        Set<FieldManager>(field, "queuedPackets", packets);
        Set<FieldManager>(field, "logger", Log.Logger);
        var handler = new DeferredHandler();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(8);
        field.QueuePacket(handler, session, new ByteReader(buffer, 0));
        using (sent) {
            session.AbortPersistence("Injected uncertain commit.");
            typeof(FieldManager).GetMethod("ProcessPackets", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(field, null);
            Assert.That(handler.Calls, Is.Zero);
            Assert.That(packets, Is.Empty);
        }
    }

    [Test]
    public async Task QuarantineDisconnectsOnlyAfterTheItemOperationUnlocks() {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var peer = new TcpClient();
        Task<TcpClient> accept = listener.AcceptTcpClientAsync();
        await peer.ConnectAsync((IPEndPoint) listener.LocalEndpoint);
        using TcpClient server = await accept;
        var builder = new ContainerBuilder();
        builder.RegisterInstance((CommandRouter) RuntimeHelpers.GetUninitializedObject(typeof(CommandRouter)));
        using IContainer container = builder.Build();
        using var session = (GameSession) Activator.CreateInstance(typeof(GameSession), server, null, container)!;
        session.Item = (ItemManager) RuntimeHelpers.GetUninitializedObject(typeof(ItemManager));
        lock (session.Item) {
            session.AbortPersistence("Injected uncertain commit.");
            Thread.Sleep(50);
            Assert.That(session.Connected(), Is.True);
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        byte[] buffer = new byte[512];
        while (await peer.GetStream().ReadAsync(buffer, timeout.Token) > 0) { }
        Assert.That(session.State, Is.EqualTo(SessionState.Disconnected));
    }

    [Test]
    public void QuarantinedFieldRemovalDetachesPlayerAndPetWithoutSavingPlots() {
        (GameSession session, var sent) = CreateSession();
        var field = (FieldManager) RuntimeHelpers.GetUninitializedObject(typeof(FieldManager));
        var players = new ConcurrentDictionary<int, FieldPlayer>();
        var pets = new ConcurrentDictionary<int, FieldPet>();
        Set<FieldManager>(field, "<Players>k__BackingField", players);
        Set<FieldManager>(field, "<Pets>k__BackingField", pets);
        Set<FieldManager>(field, "Scheduler", new EventQueue(Log.Logger));
        var player = (FieldPlayer) RuntimeHelpers.GetUninitializedObject(typeof(FieldPlayer));
        Set<Actor<Player>>(player, "<ObjectId>k__BackingField", 1);
        Set<FieldPlayer>(player, "Session", session);
        var pet = (FieldPet) RuntimeHelpers.GetUninitializedObject(typeof(FieldPet));
        Set<Actor<Npc>>(pet, "<ObjectId>k__BackingField", 2);
        Set<FieldPet>(pet, "owner", player);
        players[1] = player;
        pets[2] = pet;
        using (sent) {
            session.AbortPersistence("Injected uncertain commit.");
            Assert.That(field.RemovePlayer(1, out _), Is.True);
            Assert.That(players, Is.Empty);
            Assert.That(pets, Is.Empty);
        }
    }

    private static (GameSession, BlockingCollection<(byte[], int)>) CreateSession() {
        var session = (GameSession) RuntimeHelpers.GetUninitializedObject(typeof(GameSession));
        GC.SuppressFinalize(session);
        var packets = new BlockingCollection<(byte[], int)>();
        session.State = SessionState.Connected;
        session.Item = (ItemManager) RuntimeHelpers.GetUninitializedObject(typeof(ItemManager));
        Set<NetworkSession>(session, "Logger", Log.Logger);
        Set<NetworkSession>(session, "sendQueue", packets);
        Set<NetworkSession>(session, "lastSentPackets", new ConcurrentDictionary<SendOp, byte[]>());
        return (session, packets);
    }

    private static void Set<T>(object target, string name, object value) {
        typeof(T).GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(target, value);
    }

    private sealed class ImmediateHandler : PacketHandler<GameSession> {
        public override RecvOp OpCode => RecvOp.Quest;
        public int Calls;
        public override void Handle(GameSession session, IByteReader packet) => Calls++;
    }

    private sealed class DeferredHandler : FieldPacketHandler {
        public override RecvOp OpCode => RecvOp.Quest;
        public int Calls;
        public override void Handle(GameSession session, IByteReader packet) => Calls++;
    }
}
