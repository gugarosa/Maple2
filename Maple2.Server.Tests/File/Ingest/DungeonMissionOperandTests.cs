using System;
using Maple2.File.Ingest.Mapper;

namespace Maple2.Server.Tests.File.Ingest;

public class DungeonMissionOperandTests {
    [TestCase("", 0)]
    [TestCase("123", 123)]
    [TestCase("123.75", 123)]
    [TestCase("1e3", 1000)]
    [TestCase("-1", -1)]
    public void RawNumericOperandsPreserveExistingIntegerSemantics(string value, long expected) {
        Assert.That(TableMapper.ParseMissionValue(value), Is.EqualTo(expected));
    }

    [Test]
    public void UnsupportedOperandsDoNotSilentlyBecomeZero() {
        Assert.Throws<FormatException>(() => TableMapper.ParseMissionValue("unknown"));
        Assert.Throws<OverflowException>(() => TableMapper.ParseMissionValue("9223372036854775808"));
    }
}
