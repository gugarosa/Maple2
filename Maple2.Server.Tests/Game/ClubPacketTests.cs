using System;
using System.Buffers.Binary;
using Maple2.PacketLib.Tools;
using Maple2.Server.Game.Packets;

namespace Maple2.Server.Tests.Game;

public class ClubPacketTests {
    [TestCase(true, 13)]
    [TestCase(false, 22)]
    public void BuffSelectionPacketsUseTheCorroboratedV12Layout(bool leaderReceipt, byte command) {
        const long clubId = 1234567890123;
        const int selectorId = 2;
        const int level = 1;
        ByteWriter packet = leaderReceipt
            ? ClubPacket.ChangeBuffNotification(clubId, selectorId, level)
            : ClubPacket.ChangeBuff(clubId, selectorId, level);
        ReadOnlySpan<byte> bytes = packet.Buffer.AsSpan(0, packet.Length);

        Assert.That(packet.Length, Is.EqualTo(19));
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(bytes), Is.EqualTo(0x00F8));
        Assert.That(bytes[2], Is.EqualTo(command));
        Assert.That(BinaryPrimitives.ReadInt64LittleEndian(bytes[3..]), Is.EqualTo(clubId));
        Assert.That(BinaryPrimitives.ReadInt32LittleEndian(bytes[11..]), Is.EqualTo(selectorId));
        Assert.That(BinaryPrimitives.ReadInt32LittleEndian(bytes[15..]), Is.EqualTo(level));
    }
}
