using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Maple2.Model.Enum;
using Maple2.Server.Core.Network;

namespace Maple2.Server.Tests.Core.Network;

public class SessionTests {
    [Test]
    public async Task QueuedPacketReachesConnectedClient() {
        (TcpClient server, TcpClient peer) = await ConnectClients();
        using (server)
        using (peer)
        using (var session = new TestSession(server))
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5))) {
            session.Send([1, 0, 7]);

            var received = new byte[128];
            int count = await peer.GetStream().ReadAsync(received, timeout.Token);
            Assert.That(count, Is.GreaterThan(0));
            Assert.That(server.GetStream().WriteTimeout, Is.EqualTo(5000));
        }
    }

    [Test]
    public async Task FailedSendDisconnectsSession() {
        (TcpClient server, TcpClient peer) = await ConnectClients();
        using (server)
        using (peer)
        using (var session = new TestSession(server)) {
            server.GetStream().Dispose();
            session.Send([1, 0, 7]);

            await session.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(session.State, Is.EqualTo(SessionState.Disconnected));
        }
    }

    private static async Task<(TcpClient, TcpClient)> ConnectClients() {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint) listener.LocalEndpoint;
        var peer = new TcpClient();
        Task<TcpClient> accepted = listener.AcceptTcpClientAsync();
        await peer.ConnectAsync(endpoint.Address, endpoint.Port);
        return (await accepted, peer);
    }

    private sealed class TestSession(TcpClient client) : Session(client) {
        public readonly TaskCompletionSource<bool> Disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override PatchType Type => PatchType.Ignore;

        protected override void Dispose(bool disposing) {
            base.Dispose(disposing);
            Disposed.TrySetResult(true);
        }
    }
}
