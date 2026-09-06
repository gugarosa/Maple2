using Maple2.Model.Enum;
using Maple2.Server.Game.Trigger;

namespace Maple2.Server.Tests.Game.Trigger;

public class QuestUserDetectedTests {
    [Test]
    public void MatchesAnyRequestedState() {
        Assert.That(TriggerContext.MatchesQuestState(QuestState.Started, false, [3, 1]), Is.True);
        Assert.That(TriggerContext.MatchesQuestState(QuestState.Completed, false, [1, 3]), Is.True);
    }

    [Test]
    public void DistinguishesStartedFromReadyToComplete() {
        Assert.That(TriggerContext.MatchesQuestState(QuestState.Started, false, [1]), Is.True);
        Assert.That(TriggerContext.MatchesQuestState(QuestState.Started, true, [1]), Is.False);
        Assert.That(TriggerContext.MatchesQuestState(QuestState.Started, true, [2]), Is.True);
    }

    [Test]
    public void MatchesRequiredJobOrAnyJob() {
        Assert.That(TriggerContext.MatchesJob(JobCode.Archer, 0), Is.True);
        Assert.That(TriggerContext.MatchesJob(JobCode.Archer, (int) JobCode.Archer), Is.True);
        Assert.That(TriggerContext.MatchesJob(JobCode.Archer, (int) JobCode.Wizard), Is.False);
    }

    [TestCase(false, false, false)]
    [TestCase(false, true, true)]
    [TestCase(true, false, true)]
    [TestCase(true, true, false)]
    public void AppliesNegation(bool detected, bool negate, bool expected) {
        Assert.That(TriggerContext.ApplyNegate(detected, negate), Is.EqualTo(expected));
    }
}
