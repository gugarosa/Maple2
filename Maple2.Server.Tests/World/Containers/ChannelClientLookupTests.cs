using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Health.V1;
using Grpc.Net.Client;
using Maple2.Model.Game;
using Maple2.Server.Channel.Service;
using Maple2.Server.Core.Constants;
using Maple2.Server.World;
using Maple2.Server.World.Containers;
using ChannelClient = Maple2.Server.Channel.Service.Channel.ChannelClient;
using ServingStatus = Grpc.Health.V1.HealthCheckResponse.Types.ServingStatus;

namespace Maple2.Server.Tests.World.Containers;

public class ChannelClientLookupTests {
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [TestCase(false, "Pending")]
    [TestCase(false, "Active")]
    [TestCase(false, "Inactive")]
    [TestCase(true, "Pending")]
    [TestCase(true, "Active")]
    [TestCase(true, "Inactive")]
    public async Task ReRegistrationReplacesTransportAndMonitorButRetainsSlotAndPorts(bool instanced, string status) {
        await using var registry = new Registry();
        var firstHealth = new HealthStub(status == "Pending" ? null : status == "Active" ? ServingStatus.Serving : ServingStatus.NotServing);
        Registration first = registry.Register("game-channel", instanced, firstHealth);
        await firstHealth.Started.Task.WaitAsync(Timeout);
        if (status != "Pending") {
            await firstHealth.Completed.Task.WaitAsync(Timeout);
        }
        Assert.That(Get<object>(first.Entry, "Status").ToString(), Is.EqualTo(status));

        var nextHealth = new HealthStub(null);
        Registration next = registry.Register("GAME-CHANNEL", instanced, nextHealth);
        await Get<Task>(first.Entry, "MonitorTask").WaitAsync(Timeout);

        Assert.Multiple(() => {
            Assert.That(next.Ports, Is.EqualTo(first.Ports));
            Assert.That(next.Ports.Id, Is.EqualTo(instanced ? 0 : 1));
            Assert.That(next.Ports.GamePort, Is.EqualTo(Target.BaseGamePort + next.Ports.Id));
            Assert.That(next.Ports.GrpcPort, Is.EqualTo(Target.BaseGrpcChannelPort + next.Ports.Id));
            Assert.That(next.Entry, Is.Not.SameAs(first.Entry));
            Assert.That(next.Client, Is.Not.SameAs(first.Client));
            Assert.That(next.Health, Is.Not.SameAs(first.Health));
            Assert.That(Get<GrpcChannel>(next.Entry, "Transport"), Is.Not.SameAs(Get<GrpcChannel>(first.Entry, "Transport")));
            Assert.That(Get<CancellationToken>(first.Entry, "CancellationToken").IsCancellationRequested, Is.True);
            Assert.That(Get<CancellationToken>(next.Entry, "CancellationToken").IsCancellationRequested, Is.False);
        });
        Assert.Throws<ObjectDisposedException>(() => first.Health.CheckAsync(new HealthCheckRequest()));
        Assert.Throws<ObjectDisposedException>(() => _ = Get<CancellationTokenSource>(first.Entry, "cancellation").Token);
        AssertUnavailable(registry.Lookup, next.Ports.Id);

        nextHealth.Respond(ServingStatus.Serving);
        await nextHealth.Completed.Task.WaitAsync(Timeout);
        Assert.That(registry.Lookup.TryGetActiveEndpoint(next.Ports.Id, out var endpoint), Is.True);
        Assert.That(endpoint!.Port, Is.EqualTo(first.Ports.GamePort));
        Assert.That(registry.Lookup.TryGetClient(next.Ports.Id, out _), Is.True);
        Assert.That(registry.Lookup.Keys, Is.EqualTo(instanced ? Array.Empty<int>() : new[] { next.Ports.Id }));
    }

    [TestCase("Serving")]
    [TestCase("NotServing")]
    [TestCase("Unavailable")]
    public async Task RetiredMonitorCannotRemoveReplacementOrTakeItsPlayersOffline(string lateResponse) {
        await using var registry = new Registry();
        var oldHealth = new HealthStub(null) { IgnoreCancellation = true };
        Registration old = registry.Register("game-ch1", health: oldHealth);
        await oldHealth.Started.Task.WaitAsync(Timeout);
        var newHealth = new HealthStub();
        Registration replacement = registry.Register("game-ch1", health: newHealth);
        await newHealth.Completed.Task.WaitAsync(Timeout);
        PlayerInfo player = registry.AddPlayer(10, replacement.Ports.Id);

        if (lateResponse == "Unavailable") {
            oldHealth.Response.TrySetException(new RpcException(new Status(StatusCode.Unavailable, "Retired endpoint")));
        } else {
            oldHealth.Respond(lateResponse == "Serving" ? ServingStatus.Serving : ServingStatus.NotServing);
        }
        await Get<Task>(old.Entry, "MonitorTask").WaitAsync(Timeout);

        Assert.Multiple(() => {
            Assert.That(registry.Lookup.ValidChannel(replacement.Ports.Id), Is.True);
            Assert.That(registry.Lookup.Count, Is.EqualTo(1));
            Assert.That(registry.Lookup.Keys, Is.EqualTo(new[] { replacement.Ports.Id }));
            Assert.That(registry.Entry(replacement.Ports.Id), Is.SameAs(replacement.Entry));
            Assert.That(player.Channel, Is.EqualTo(replacement.Ports.Id));
            Assert.That(Get<Task>(replacement.Entry, "MonitorTask").IsCompleted, Is.False);
        });
        Assert.That(registry.Lookup.TryGetActiveEndpoint(replacement.Ports.Id, out _), Is.True);
        Assert.That(registry.Lookup.TryGetClient(replacement.Ports.Id, out _), Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PendingAndInactiveChannelsRejectAdmissionAndKeepMonitoringUntilServing(bool instanced) {
        await using var registry = new Registry();
        var health = new HealthStub(null);
        Registration registration = registry.Register("game-channel", instanced, health);
        CallOptions options = await health.Started.Task.WaitAsync(Timeout);
        AssertUnavailable(registry.Lookup, registration.Ports.Id);
        Assert.That(options.Deadline, Is.Not.Null);
        Assert.That(options.Deadline, Is.LessThanOrEqualTo(DateTime.UtcNow.AddSeconds(5)));
        Assert.That(options.CancellationToken.CanBeCanceled, Is.True);

        health.Respond(ServingStatus.NotServing);
        await health.Completed.Task.WaitAsync(Timeout);
        Assert.That(Get<object>(registration.Entry, "Status").ToString(), Is.EqualTo("Inactive"));
        AssertUnavailable(registry.Lookup, registration.Ports.Id);
        Assert.That(Get<Task>(registration.Entry, "MonitorTask").IsCompleted, Is.False);

        // Subsequent health checks serve again without a new registration, including in Release.
        Assert.That(() => registry.Lookup.ValidChannel(registration.Ports.Id), Is.True.After(10000, 20));
        Assert.That(registry.Lookup.Count, Is.EqualTo(instanced ? 0 : 1));
        Assert.That(registry.Lookup.TryGetInstancedChannelId(out int instancedId), Is.EqualTo(instanced));
        Assert.That(instancedId, Is.EqualTo(instanced ? 0 : -1));
    }

    [Test]
    public async Task DistinctGrpcHostsSharingPublicIpKeepSeparateSlotsEvenWhenInactive() {
        await using var registry = new Registry();
        var firstHealth = new HealthStub(ServingStatus.NotServing);
        Registration first = registry.Register("game-ch1", health: firstHealth);
        await firstHealth.Completed.Task.WaitAsync(Timeout);
        Registration second = registry.Register("game-ch2", health: new HealthStub(null));
        Assert.Multiple(() => {
            Assert.That(first.Ports.Id, Is.EqualTo(1));
            Assert.That(second.Ports.Id, Is.EqualTo(2));
            Assert.That(second.Ports.GamePort, Is.EqualTo(Target.BaseGamePort + 2));
            Assert.That(second.Ports.GrpcPort, Is.EqualTo(Target.BaseGrpcChannelPort + 2));
            Assert.That(registry.Entry(1), Is.SameAs(first.Entry));
            Assert.That(Get<CancellationToken>(first.Entry, "CancellationToken").IsCancellationRequested, Is.False);
        });

        Registration firstRestart = registry.Register("game-ch1", health: new HealthStub(null));
        Assert.That(firstRestart.Ports, Is.EqualTo(first.Ports));
        Assert.That(registry.Entry(2), Is.SameAs(second.Entry));
    }

    [Test]
    public async Task InstancedSlotCannotBeTakenByAnotherHost() {
        await using var registry = new Registry();
        Registration instanced = registry.Register("game-ch0", true, new HealthStub(null));
        RpcException? error = Assert.Throws<RpcException>(() => registry.Register("other-instance", true, new HealthStub(null)));
        Assert.That(error!.StatusCode, Is.EqualTo(StatusCode.AlreadyExists));
        Assert.That(registry.Entry(0), Is.SameAs(instanced.Entry));
        Assert.That(Get<CancellationToken>(instanced.Entry, "CancellationToken").IsCancellationRequested, Is.False);
        Assert.That(registry.Register("game-ch1", health: new HealthStub(null)).Ports.Id, Is.EqualTo(1));
    }

    [Test]
    public async Task ConcurrentRegistrationsPreserveDistinctEndpointSlots() {
        await using var registry = new Registry();
        Registration[] registrations = await Task.WhenAll(Enumerable.Range(1, 8).Select(id =>
            Task.Run(() => registry.Register($"game-ch{id}", health: new HealthStub(null))))).WaitAsync(Timeout);
        Assert.That(registrations.Select(entry => entry.Ports.Id), Is.EquivalentTo(Enumerable.Range(1, 8)));

        Registration[] replacements = await Task.WhenAll(registrations.Select(entry => Task.Run(() => {
            string host = Get<string>(entry.Entry, "GrpcHost");
            for (int attempt = 0; attempt < 20; attempt++) {
                registry.Lookup.TryGetClient(entry.Ports.Id, out _);
                registry.Lookup.TryGetActiveEndpoint(entry.Ports.Id, out _);
                registry.Lookup.ValidChannel(entry.Ports.Id);
            }
            Registration replacement = registry.Register(host, health: new HealthStub(null));
            Assert.That(replacement.Ports, Is.EqualTo(entry.Ports));
            return replacement;
        }))).WaitAsync(Timeout);
        Assert.That(replacements.Select(entry => entry.Ports.Id), Is.EquivalentTo(Enumerable.Range(1, 8)));
    }

    [Test]
    public async Task FailingPeerNotificationsDoNotDeactivateHealthyOwnerOrInterruptOfflineCleanup() {
        await using var registry = new Registry();
        var peerClient = new ChannelStub { FailNotifications = true };
        var peerHealth = new HealthStub();
        Registration peer = registry.Register("game-peer", health: peerHealth, client: peerClient);
        await peerHealth.Completed.Task.WaitAsync(Timeout);
        var ownerHealth = new HealthStub(null);
        var ownerClient = new ChannelStub();
        Registration owner = registry.Register("game-owner", health: ownerHealth, client: ownerClient);
        await ownerHealth.Started.Task.WaitAsync(Timeout);
        PlayerInfo first = registry.AddPlayer(1, owner.Ports.Id);
        PlayerInfo second = registry.AddPlayer(2, owner.Ports.Id);

        Invoke(registry.Lookup, "Inactive", owner.Entry);
        Assert.That(new[] { first.Channel, second.Channel }, Is.All.EqualTo(-1));
        registry.Boards[1] = "Welcome back";
        Invoke(registry.Lookup, "Active", owner.Entry);

        Assert.Multiple(() => {
            Assert.That(registry.Lookup.ValidChannel(owner.Ports.Id), Is.True);
            Assert.That(registry.Lookup.ValidChannel(peer.Ports.Id), Is.True);
            Assert.That(peerClient.Calls.Count(call => call.Method == "UpdatePlayer"), Is.EqualTo(2));
            Assert.That(peerClient.Calls.Any(call => call.Method == "UpdateChannels"), Is.True);
            Assert.That(ownerClient.Calls.Select(call => call.Method), Does.Contain("Admin"));
            Assert.That(Get<Task>(owner.Entry, "MonitorTask").IsCompleted, Is.False);
        });
        foreach ((string _, CallOptions options) in peerClient.Calls.Concat(ownerClient.Calls)) {
            Assert.That(options.Deadline, Is.Not.Null);
            Assert.That(options.Deadline, Is.LessThanOrEqualTo(DateTime.UtcNow.AddSeconds(5)));
            Assert.That(options.CancellationToken, Is.EqualTo(Get<CancellationToken>(owner.Entry, "CancellationToken")));
        }
    }

    [TestCase("", "game-ch1")]
    [TestCase("not-an-ip", "game-ch1")]
    [TestCase("203.0.113.10", "")]
    [TestCase("203.0.113.10", "http://game-ch1")]
    [TestCase("203.0.113.10", "game-ch1/path")]
    [TestCase("203.0.113.10", "game-ch1:21003")]
    public void InvalidRegistrationReturnsExplicitRpcError(string gameIp, string grpcHost) {
        using var lookup = new ChannelClientLookup();
        RpcException? error = Assert.Throws<RpcException>(() => lookup.FindOrCreateChannelByIp(gameIp, grpcHost, false));
        Assert.That(error!.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
        Assert.That(lookup.Count, Is.Zero);
    }

    private static void AssertUnavailable(ChannelClientLookup lookup, int id) {
        Assert.Multiple(() => {
            Assert.That(lookup.ValidChannel(id), Is.False);
            Assert.That(lookup.TryGetClient(id, out _), Is.False);
            Assert.That(lookup.TryGetActiveEndpoint(id, out _), Is.False);
            Assert.That(lookup.Count, Is.Zero);
            Assert.That(lookup.Keys, Is.Empty);
            Assert.That(lookup, Is.Empty);
            Assert.That(lookup.FirstChannel(), Is.EqualTo(-1));
            Assert.That(lookup.TryGetInstancedChannelId(out _), Is.False);
        });
    }

    private static T Get<T>(object target, string name) {
        return (T) Field(target, name).GetValue(target)!;
    }

    private static System.Reflection.FieldInfo Field(object target, string name) {
        return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing test field {target.GetType().Name}.{name}.");
    }

    private static void Invoke(object target, string method, object argument) {
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, [argument]);
    }

    private sealed record Registration((ushort GamePort, int GrpcPort, int Id) Ports, object Entry, ChannelClient Client, Health.HealthClient Health);

    private sealed class Registry : IAsyncDisposable {
        public readonly ChannelClientLookup Lookup = new();
        public readonly ConcurrentDictionary<int, string> Boards = new();
        private readonly ConcurrentDictionary<long, PlayerInfo> players = new();
        private readonly List<Registration> registrations = new();
        private readonly List<HealthStub> healthClients = new();

        public Registry() {
            // Populate only the in-memory dependencies; do not start World threads or connect to a database.
            var world = (WorldServer) RuntimeHelpers.GetUninitializedObject(typeof(WorldServer));
            Field(world, "memoryStringBoards").SetValue(world, Boards);
            var playerLookup = (PlayerInfoLookup) RuntimeHelpers.GetUninitializedObject(typeof(PlayerInfoLookup));
            Field(playerLookup, "cache").SetValue(playerLookup, players);
            Lookup.InjectDependencies(world, playerLookup);
        }

        public Registration Register(string host, bool instanced = false, HealthStub? health = null, ChannelStub? client = null) {
            lock (Get<object>(Lookup, "sync")) {
                var ports = Lookup.FindOrCreateChannelByIp("203.0.113.10", host, instanced);
                object entry = Entry(ports.channel);
                var registration = new Registration(ports, entry, Get<ChannelClient>(entry, "Client"), Get<Health.HealthClient>(entry, "Health"));
                // The monitor's ownership check takes this same lock before its first RPC. No real socket or DNS is used.
                health ??= new HealthStub();
                Field(entry, "Health").SetValue(entry, health);
                Field(entry, "Client").SetValue(entry, client ?? new ChannelStub());
                registrations.Add(registration);
                healthClients.Add(health);
                return registration;
            }
        }

        public object Entry(int id) {
            return Get<IDictionary>(Lookup, "channels")[id]!;
        }

        public PlayerInfo AddPlayer(long id, int channel) {
            var player = new PlayerInfo(new CharacterInfo(id, id, "Player", "", "", default, default, 1), "Home", default, []) {
                Channel = (short) channel,
            };
            players[id] = player;
            return player;
        }

        public async ValueTask DisposeAsync() {
            Lookup.Dispose();
            foreach (HealthStub health in healthClients) {
                health.Response.TrySetCanceled();
            }
            await Task.WhenAll(registrations.Select(entry => Get<Task>(entry.Entry, "MonitorTask"))).WaitAsync(Timeout);
        }
    }

    private sealed class HealthStub : Health.HealthClient {
        public readonly TaskCompletionSource<HealthCheckResponse> Response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<CallOptions> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> Completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IgnoreCancellation;
        private int calls;

        public HealthStub(ServingStatus? status = ServingStatus.Serving) {
            if (status.HasValue) {
                Respond(status.Value);
            }
        }

        public void Respond(ServingStatus status) {
            Response.TrySetResult(new HealthCheckResponse { Status = status });
        }

        public override AsyncUnaryCall<HealthCheckResponse> CheckAsync(HealthCheckRequest request, CallOptions options) {
            Started.TrySetResult(options);
            Task<HealthCheckResponse> response = Interlocked.Increment(ref calls) == 1
                ? Response.Task
                : Task.FromResult(new HealthCheckResponse { Status = ServingStatus.Serving });
            return new AsyncUnaryCall<HealthCheckResponse>(
                IgnoreCancellation ? response : response.WaitAsync(options.CancellationToken),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(),
                () => Completed.TrySetResult(true));
        }
    }

    private sealed class ChannelStub : ChannelClient {
        public readonly ConcurrentQueue<(string Method, CallOptions Options)> Calls = new();
        public bool FailNotifications;

        public override ChannelsUpdateResponse UpdateChannels(ChannelsUpdateRequest request, CallOptions options) {
            Notify("UpdateChannels", options);
            return new ChannelsUpdateResponse();
        }

        public override PlayerUpdateResponse UpdatePlayer(PlayerUpdateRequest request, CallOptions options) {
            Notify("UpdatePlayer", options);
            return new PlayerUpdateResponse();
        }

        public override AdminResponse Admin(AdminRequest request, CallOptions options) {
            Notify("Admin", options);
            return new AdminResponse();
        }

        private void Notify(string method, CallOptions options) {
            Calls.Enqueue((method, options));
            if (FailNotifications) {
                throw new RpcException(new Status(StatusCode.Unavailable, "Peer unavailable"));
            }
        }
    }
}
