using Maple2.File.Ingest.Mapper;

namespace Maple2.Server.Tests.File.Ingest;

public class SkillMapperTests {
    [TestCase(90000409, 1, false, true)]
    [TestCase(90000409, 1, true, true)]
    [TestCase(90000409, 2, false, false)]
    [TestCase(90000037, 1, true, true)]
    [TestCase(10000001, 1, false, false)]
    [TestCase(10000001, 1, true, true)]
    public void PreservesConsumptionExceptForKnownPremiumPotionOmission(
        int skillId, short level, bool configured, bool expected) {
        Assert.That(SkillMapper.UsesItem(skillId, level, configured), Is.EqualTo(expected));
    }
}
