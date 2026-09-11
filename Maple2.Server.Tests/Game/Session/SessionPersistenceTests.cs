using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Maple2.Model.Enum;
using Maple2.PacketLib.Tools;
using Maple2.Server.Core.Constants;
using Maple2.Server.Game.PacketHandlers;
using Maple2.Server.Game.Manager.Items;
using Maple2.Server.Game.Session;
using Maple2.Server.Login.Session;
using Maple2.Server.World.Service;
using Maple2.Tools.Scheduler;
using Serilog;
using NetworkSession = Maple2.Server.Core.Network.Session;
using WorldClient = Maple2.Server.World.Service.World.WorldClient;

namespace Maple2.Server.Tests.Game.Session;

public class SessionPersistenceTests {
    [TestCase(false)]
    [TestCase(true)]
    public void AcquisitionExhaustionCannotSaveOrReleaseAnotherHolder(bool migrating) {
        var world = new WorldStub { Busy = true };
        (GameSession session, var packets) = CreateSession<GameSession>(world);
        using (packets) {
            Assert.That(migrating ? session.MigrationSave() : session.SessionSave(), Is.False);
            Assert.That(Get<bool>(session, "preMigrationSaved"), Is.False);
            Assert.That(world.Acquisitions, Has.Count.EqualTo(5));
            Assert.That(world.Releases, Is.Empty);
            Assert.That(world.Acquisitions.ConvertAll(entry => entry.OwnerToken), Is.All.EqualTo(world.Acquisitions[0].OwnerToken));
            Assert.That(Guid.TryParse(world.Acquisitions[0].OwnerToken, out Guid owner) && owner != Guid.Empty, Is.True);
        }
    }

    [Test]
    public void FailedSaveReleasesOnlyItsAcquiredLeaseAndDoesNotSetMigrationMarker() {
        var world = new WorldStub { Allow = true };
        (GameSession session, var packets) = CreateSession<GameSession>(world);
        using (packets) {
            // No Player/database is installed: failure occurs after acquiring the lease, before a commit.
            Assert.That(session.MigrationSave(), Is.False);
            Assert.That(Get<bool>(session, "preMigrationSaved"), Is.False);
            Assert.That(world.Releases, Has.Count.EqualTo(1));
            Assert.That(world.Releases[0], Is.EqualTo(world.Acquisitions[0]));

            Assert.That(session.MigrationSave(), Is.False);
            Assert.That(world.Acquisitions[1].OwnerToken, Is.Not.EqualTo(world.Acquisitions[0].OwnerToken));
            Assert.That(world.Releases[1], Is.EqualTo(world.Acquisitions[1]));
        }
    }

    [Test]
    public void ConfirmedMigrationSaveIsIdempotentButCannotBeOverwrittenByALaterSessionSave() {
        var world = new WorldStub();
        (GameSession session, var packets) = CreateSession<GameSession>(world);
        using (packets) {
            Set(session, "preMigrationSaved", true);
            Assert.That(session.MigrationSave(), Is.True);
            Assert.That(session.SessionSave(), Is.False);
            Assert.That(world.Acquisitions, Is.Empty);
            Assert.That(world.Releases, Is.Empty);
        }
    }

    [Test]
    public void SaveHoldsItemLockThroughLeaseAcquisitionAndRelease() {
        var world = new WorldStub { Allow = true };
        (GameSession session, var packets) = CreateSession<GameSession>(world);
        using (packets) {
            var lockStates = new List<(bool Items, bool Save)>();
            world.OnLeaseCall = () => lockStates.Add((Monitor.IsEntered(session.Item), Monitor.IsEntered(Get<object>(session, "saveSync"))));
            Assert.That(session.SessionSave(), Is.False);
            Assert.That(lockStates, Is.EqualTo(new[] { (true, true), (true, true) }));
            lock (session.Item) {
                Assert.That(session.SessionSave(), Is.False);
            }
            Assert.That(lockStates, Is.All.EqualTo((true, true)));
        }
    }

    [Test]
    public async Task QuarantinePreventsAWaitingSaveAndAnyLaterMigrationSave() {
        var world = new WorldStub { Allow = true };
        (GameSession session, var packets) = CreateSession<GameSession>(world);
        using (packets) {
            Task<bool> save;
            lock (session.Item) {
                save = Task.Run(session.SessionSave);
                session.AbortPersistence("Injected uncertain purchase commit.");
            }
            Assert.That(await save.WaitAsync(TimeSpan.FromSeconds(10)), Is.False);
            Set(session, "preMigrationSaved", true);
            Assert.That(session.PersistenceAborted, Is.True);
            Assert.That(session.MigrationSave(), Is.False);
            Assert.That(session.SessionSave(), Is.False);
            Assert.That(world.Acquisitions, Is.Empty);
            Assert.That(world.Releases, Is.Empty);
        }
    }

    [Test]
    public void FailedAcquisitionPreventsPlayerLoadingAndServerAdmission() {
        var world = new WorldStub();
        (GameSession session, var packets) = CreateSession<GameSession>(world);
        using (packets) {
            Assert.That(session.EnterServer(123, Guid.NewGuid(), new MigrateInResponse { CharacterId = 456 }), Is.False);
            Assert.That(world.Releases, Is.Empty);
            Assert.That(session.Player, Is.Null);
            Assert.That(packets.Count, Is.EqualTo(1));
        }
    }

    [Test]
    public void FailedPersistencePreventsChannelAndPlannerHandoffs() {
        var world = new WorldStub();
        (GameSession session, var packets) = CreateSession<GameSession>(world);
        using (packets) {
            new ChannelHandler { World = world }.Handle(session, new ByteReader(BitConverter.GetBytes((short) 2), 0));
            session.MigrateToPlanner(PlotMode.Normal);
            Assert.That(world.Migrations, Is.Zero);
            Assert.That(world.Releases, Is.Empty);
            Assert.That(Get<bool>(session, "preMigrationSaved"), Is.False);
            Assert.That(packets.Count, Is.EqualTo(2));
        }
    }

    [Test]
    public void ItemTeardownFailurePreventsHandoffAndPreservesTheManagerReference() {
        var world = new WorldStub { Allow = true };
        (GameSession session, var packets) = CreateSession<GameSession>(world);
        using (packets) {
            var storage = (StorageManager) RuntimeHelpers.GetUninitializedObject(typeof(StorageManager));
            session.Storage = storage;
            session.MigrateToPlanner(PlotMode.Normal);
            Assert.That(session.PersistenceAborted, Is.True);
            Assert.That(session.Storage, Is.SameAs(storage));
            Assert.That(Get<bool>(session, "preMigrationSaved"), Is.False);
            Assert.That(world.Acquisitions, Is.Empty);
            Assert.That(world.Migrations, Is.Zero);
            Assert.That(packets.Count, Is.EqualTo(1));
        }
    }

    [Test]
    public void MigrationDrainsNestedCommittedEffectsBeforeAcquiringTheAccountLease() {
        var world = new WorldStub { Allow = true };
        (GameSession session, var packets) = CreateSession<GameSession>(world);
        using (packets) {
            int calls = 0;
            lock (session.Item) {
                session.Item.AfterUnlock(() => {
                    Assert.That(Monitor.IsEntered(session.Item), Is.False);
                    Assert.That(Monitor.IsEntered(Get<object>(session, "saveSync")), Is.False);
                    Assert.That(world.Acquisitions, Is.Empty);
                    calls++;
                    session.Item.AfterUnlock(() => calls++);
                });
            }
            session.Scheduler.Stop();
            world.OnLeaseCall = () => Assert.That(calls, Is.EqualTo(2));
            Assert.That(session.MigrationSave(), Is.False, "The synthetic player is absent, but callbacks must finish before the save is attempted.");
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(session.Scheduler.Queued, Is.Zero);
        }
    }

    [Test]
    public void MigrationCannotPumpCallbacksWhileTheCallerOwnsItem() {
        var world = new WorldStub { Allow = true };
        (GameSession session, var packets) = CreateSession<GameSession>(world);
        using (packets) {
            bool invoked = false;
            lock (session.Item) {
                session.Item.AfterUnlock(() => invoked = true);
                Assert.That(session.MigrationSave(), Is.False);
                Assert.That(invoked, Is.False);
                Assert.That(world.Acquisitions, Is.Empty);
            }
            session.Scheduler.DrainImmediate();
            Assert.That(invoked, Is.True);
        }
    }

    [Test]
    public void FailedDeferredEffectQuarantinesInsteadOfSendingAHandoff() {
        var world = new WorldStub { Allow = true };
        (GameSession session, var packets) = CreateSession<GameSession>(world);
        using (packets) {
            session.Item.AfterUnlock(() => throw new InvalidOperationException("Injected committed-effect failure."));
            session.MigrateToPlanner(PlotMode.Normal);
            Assert.That(session.PersistenceAborted, Is.True);
            Assert.That(Get<bool>(session, "preMigrationSaved"), Is.False);
            Assert.That(world.Acquisitions, Is.Empty);
            Assert.That(world.Migrations, Is.Zero);
            Assert.That(session.Scheduler.Queued, Is.Zero, "Quarantine must not schedule a second item-owned disconnect.");
        }
    }

    [Test]
    public void LoginLockExhaustionDoesNotReadDataOrReleaseTheHolder() {
        var world = new WorldStub { Busy = true };
        (LoginSession session, var packets) = CreateSession<LoginSession>(world);
        using (packets) {
            RpcException? error = Assert.Throws<RpcException>(() => session.ListCharacters());
            Assert.That(error!.StatusCode, Is.EqualTo(StatusCode.Unavailable));
            Assert.That(world.Acquisitions, Has.Count.EqualTo(3));
            Assert.That(world.Releases, Is.Empty);
            Assert.That(packets, Is.Empty);
        }
    }

    [Test]
    public void LoginLoadFailureReleasesItsOwnLeaseAndReturnsAnExplicitFailure() {
        var world = new WorldStub { Allow = true };
        (LoginSession session, var packets) = CreateSession<LoginSession>(world);
        using (packets) {
            RpcException? error = Assert.Throws<RpcException>(() => session.ListCharacters());
            Assert.That(error!.StatusCode, Is.EqualTo(StatusCode.Internal));
            Assert.That(world.Releases, Has.Count.EqualTo(1));
            Assert.That(world.Releases[0], Is.EqualTo(world.Acquisitions[0]));
            Assert.That(packets, Is.Empty);
        }
    }

    [Test]
    public void ComponentFailureCannotBeSilentlyAccepted() {
        long expiresAt = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeMilliseconds();
        MethodInfo method = typeof(GameSession).GetMethod("RequireSaveSuccess", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.DoesNotThrow(() => method.Invoke(null, [true, "Player", expiresAt]));
        TargetInvocationException? failure = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [false, "Quest", expiresAt]));
        Assert.That(failure!.InnerException, Is.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void ExpiredLeaseCannotCommitEvenWhenTheComponentReportedSuccess() {
        MethodInfo method = typeof(GameSession).GetMethod("RequireSaveSuccess", BindingFlags.Static | BindingFlags.NonPublic)!;
        TargetInvocationException? failure = Assert.Throws<TargetInvocationException>(() =>
            method.Invoke(null, [true, "Item", DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds()]));
        Assert.That(failure!.InnerException, Is.TypeOf<RpcException>());
        Assert.That(((RpcException) failure.InnerException!).StatusCode, Is.EqualTo(StatusCode.DeadlineExceeded));
    }

    private static (T Session, BlockingCollection<(byte[] Packet, int Length)> Packets) CreateSession<T>(WorldStub world) where T : NetworkSession {
        var session = (T) RuntimeHelpers.GetUninitializedObject(typeof(T));
        GC.SuppressFinalize(session);
        var packets = new BlockingCollection<(byte[], int)>();
        typeof(NetworkSession).GetField("Logger", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, Log.Logger);
        typeof(NetworkSession).GetField("sendQueue", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, packets);
        typeof(NetworkSession).GetField("lastSentPackets", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(session, new ConcurrentDictionary<SendOp, byte[]>());
        typeof(NetworkSession).GetField("<AccountId>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, 123L);
        Set(session, "<World>k__BackingField", world);
        if (session is GameSession game) {
            Set(game, "saveSync", new object());
            game.Item = (ItemManager) RuntimeHelpers.GetUninitializedObject(typeof(ItemManager));
            typeof(ItemManager).GetField("session", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(game.Item, game);
            var scheduler = new EventQueue(Log.Logger);
            scheduler.Start();
            typeof(GameSession).GetField("Scheduler", BindingFlags.Instance | BindingFlags.Public)!.SetValue(game, scheduler);
            game.State = SessionState.Disconnected;
        } else {
            Set(session, "<AccountId>k__BackingField", 123L);
        }
        return (session, packets);
    }

    private static T Get<T>(object target, string field) {
        return (T) target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    }

    private static void Set(object target, string field, object value) {
        target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    }

    private sealed class WorldStub : WorldClient {
        public bool Busy;
        public bool Allow;
        public readonly List<LockRequest> Acquisitions = new();
        public readonly List<LockRequest> Releases = new();
        public int Migrations;
        public Action? OnLeaseCall;

        public override LockResponse AcquireLock(LockRequest request, CallOptions options) {
            OnLeaseCall?.Invoke();
            Assert.That(options.Deadline, Is.Not.Null);
            Acquisitions.Add(request.Clone());
            if (Busy) {
                return new LockResponse { Error = "Another owner holds the lease." };
            }
            if (!Allow) {
                throw new RpcException(new Status(StatusCode.PermissionDenied, "Injected acquisition failure."));
            }
            return new LockResponse { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeMilliseconds() };
        }

        public override LockResponse ReleaseLock(LockRequest request, CallOptions options) {
            OnLeaseCall?.Invoke();
            Assert.That(options.Deadline, Is.Not.Null);
            Releases.Add(request.Clone());
            return new LockResponse();
        }

        public override MigrateOutResponse MigrateOut(MigrateOutRequest request, CallOptions options) {
            Migrations++;
            throw new InvalidOperationException("A failed save must never request a handoff.");
        }
    }
}
