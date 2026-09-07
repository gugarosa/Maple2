using Maple2.Server.Game.Model.ActorStateComponent;
using static Maple2.Server.Game.Model.ActorStateComponent.AiState;

namespace Maple2.Server.Tests.Game;

public class AiTreeTransitionTests {
    [Test]
    public void LeavingCombatRunsBattleEndInsteadOfReturningImmediately() {
        var transition = AiState.GetTreeTransition(DecisionTreeType.Battle, false, true, true, false);

        Assert.That(transition.Tree, Is.EqualTo(DecisionTreeType.BattleEnd));
        Assert.That(transition.ResetStack, Is.True);
        Assert.That(transition.Process, Is.True);
    }

    [Test]
    public void BattleEndContinuesPendingActionsAndStopsAfterTheyFinish() {
        var pending = AiState.GetTreeTransition(DecisionTreeType.BattleEnd, false, true, true, true);
        var finished = AiState.GetTreeTransition(DecisionTreeType.BattleEnd, false, true, true, false);

        Assert.That(pending, Is.EqualTo((DecisionTreeType.BattleEnd, false, true)));
        Assert.That(finished, Is.EqualTo((DecisionTreeType.None, true, false)));
    }

    [Test]
    public void ReenteringCombatDiscardsBattleEndWork() {
        var transition = AiState.GetTreeTransition(DecisionTreeType.BattleEnd, true, true, true, true);

        Assert.That(transition, Is.EqualTo((DecisionTreeType.Battle, true, true)));
    }

    [Test]
    public void MissingEndTreeStopsWhileCombatCanStillEvaluateReservedNodes() {
        var noEnd = AiState.GetTreeTransition(DecisionTreeType.Battle, false, true, false, false);
        var reservedOnly = AiState.GetTreeTransition(DecisionTreeType.None, true, false, false, false);

        Assert.That(noEnd, Is.EqualTo((DecisionTreeType.None, true, false)));
        Assert.That(reservedOnly, Is.EqualTo((DecisionTreeType.None, false, true)));
    }
}
