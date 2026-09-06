using Maple2.Model.Enum;
using Maple2.Server.Core.Formulas;

namespace Maple2.Server.Tests.Core.Formulas;

public class BonusAttackTests {
    [TestCase(JobCode.Berserker, 1.354)]
    [TestCase(JobCode.Wizard, 1.398)]
    [TestCase(JobCode.Archer, 1.143)]
    [TestCase(JobCode.HeavyGunner, 1.364)]
    [TestCase(JobCode.RuneBlader, 1.259)]
    [TestCase(JobCode.Striker, 1.264)]
    [TestCase(JobCode.SoulBinder, 1.177)]
    public void TwoHandedWeaponUsesFullJobCoefficient(JobCode job, double multiplier) {
        Assert.That(BonusAttack.Coefficient(5, 0, job), Is.EqualTo(4.96 * multiplier).Within(0.000001));
    }

    [Test]
    public void DualWieldAveragesWeaponRarities() {
        Assert.That(BonusAttack.Coefficient(4, 5, JobCode.Thief), Is.EqualTo(4.28172).Within(0.000001));
    }

    [Test]
    public void MissingMainHandHasNoWeaponBonus() {
        Assert.That(BonusAttack.Coefficient(0, 5, JobCode.Knight), Is.Zero);
    }
}
