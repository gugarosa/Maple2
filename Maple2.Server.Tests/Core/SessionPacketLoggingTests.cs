using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Maple2.Server.Core.Constants;
using Maple2.Server.Game.Session;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using NetworkSession = Maple2.Server.Core.Network.Session;

namespace Maple2.Server.Tests.Core;

public class SessionPacketLoggingTests {
    [TestCase(RecvOp.ResponseLogin, 0)]
    [TestCase(RecvOp.ResponseKey, 0)]
    [TestCase(RecvOp.Quest, 1)]
    public void VerbosePacketTracesExcludeAuthenticationPayloads(RecvOp opcode, int expectedCount) {
        var sink = new PacketLogSink();
        using Logger logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        var session = (GameSession) RuntimeHelpers.GetUninitializedObject(typeof(GameSession));
        GC.SuppressFinalize(session);
        typeof(NetworkSession).GetField("Logger", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(session, logger);
        byte[] packet = [(byte) ((ushort) opcode & 0xff), (byte) ((ushort) opcode >> 8), 0x12, 0x34];

        typeof(NetworkSession).GetMethod("LogRecv", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session, [packet]);

        Assert.That(sink.Events, Has.Count.EqualTo(expectedCount));
    }

    private sealed class PacketLogSink : ILogEventSink {
        public readonly List<LogEvent> Events = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
