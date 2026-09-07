using System;
using System.Linq;
using System.Xml;
using Maple2.Server.Game.Trigger;
using Maple2.Server.Game.Trigger.Helpers;
using Maple2.Server.Game.Util;
using TriggerDefinition = Maple2.Server.Game.Trigger.Helpers.Trigger;

namespace Maple2.Server.Tests.Game.Trigger;

public class CompositeTriggerTests {
    [TestCase(">", OperatorType.Greater)]
    [TestCase(">=", OperatorType.GreaterEqual)]
    [TestCase("=", OperatorType.Equal)]
    [TestCase("==", OperatorType.Equal)]
    [TestCase("<=", OperatorType.LessEqual)]
    [TestCase("<", OperatorType.Less)]
    [TestCase(" greaterEqual ", OperatorType.GreaterEqual)]
    [TestCase(null, OperatorType.GreaterEqual)]
    public void ParsesSourceComparisonOperators(string? value, OperatorType expected) {
        Assert.That(TriggerFunctionMapping.ParseOperatorType(value), Is.EqualTo(expected));
    }

    [Test]
    public void ParsesDotSeparatedSourceSpawnIdsWithoutInventingIntermediateIds() {
        Assert.That(TriggerFunctionMapping.ParseIntArray("8300.8302.8312"),
            Is.EqualTo(new[] { 8300, 8302, 8312 }));
    }

    [TestCase("All")]
    [TestCase("all")]
    [TestCase(" ALL ")]
    public void ParsesTheSourceAllSelectorWithoutCaseSensitivity(string value) {
        Assert.That(TriggerFunctionMapping.ParseIntArray(value), Is.EqualTo(new[] { -1 }));
    }

    [Test]
    public void BindsCanonicalNpcDamageThreshold() {
        TriggerDefinition trigger = Parse("""
            <condition name="npc_damage" spawn_id="102" damage_rate="1.0" operator="GreaterEqual">
              <transition state="complete" />
            </condition>
            """);

        var condition = (TriggerDefinition.NpcDamage) trigger.States[0].Conditions.Single();
        Assert.That(condition.DamageRate, Is.EqualTo(1.0f));
    }

    [Test]
    public void BindsCanonicalWeddingHallState() {
        TriggerDefinition trigger = Parse("""
            <condition name="wedding_hall_state" hall_state="weddingComplete" success="1">
              <transition state="complete" />
            </condition>
            """);

        var condition = (TriggerDefinition.WeddingHallState) trigger.States[0].Conditions.Single();
        Assert.That(condition.HallState, Is.EqualTo("weddingComplete"));
    }

    [Test]
    public void AnyOneLoadsPredicatesAndKeepsOuterActionsAndTransition() {
        TriggerDefinition trigger = Parse("""
            <condition name="any_one">
              <group>
                <condition name="any_one"><group /></condition>
                <condition name="always" />
              </group>
              <action name="set_user_value" key="unlocked" value="1" trigger_id="1" />
              <transition state="complete" />
            </condition>
            """);
        var condition = (TriggerDefinition.GroupAnyOne) trigger.States[0].Conditions.Single();
        Assert.That(condition.Conditions, Has.Count.EqualTo(2));
        Assert.That(condition.Actions.Single(), Is.TypeOf<TriggerDefinition.SetUserValue>());
        Assert.That(condition.NextState, Is.EqualTo("complete"));

        int executed = 0;
        condition.Actions.Clear();
        condition.Actions.AddLast(new RecordingAction(() => executed++));
        TriggerState? next = new TriggerState(trigger, null!).OnTick();

        Assert.That(next?.Name, Is.EqualTo("complete"));
        Assert.That(executed, Is.EqualTo(1));
    }

    [Test]
    public void AllOfDoesNotRunActionsOrTransitionUntilEveryPredicateMatches() {
        TriggerDefinition trigger = Parse("""
            <condition name="all_of">
              <group>
                <condition name="always" />
                <condition name="any_one"><group /></condition>
              </group>
              <transition state="complete" />
            </condition>
            """);
        var condition = (TriggerDefinition.GroupAllOf) trigger.States[0].Conditions.Single();
        Assert.That(condition.Conditions, Has.Count.EqualTo(2));
        int executed = 0;
        condition.Actions.AddLast(new RecordingAction(() => executed++));

        var state = new TriggerState(trigger, null!);
        Assert.That(state.OnTick(), Is.Null);
        Assert.That(executed, Is.Zero);

        var nested = (TriggerDefinition.GroupAnyOne) condition.Conditions.Last!.Value;
        nested.Conditions.AddLast(new TriggerDefinition.GroupAlways());
        Assert.That(state.OnTick()?.Name, Is.EqualTo("complete"));
        Assert.That(executed, Is.EqualTo(1));
    }

    [Test]
    public void NestedGroupsRetainTheirBooleanStructure() {
        TriggerDefinition trigger = Parse("""
            <condition name="any_one">
              <group>
                <condition name="all_of">
                  <group>
                    <condition name="always" />
                    <condition name="any_one"><group /></condition>
                  </group>
                </condition>
                <condition name="all_of">
                  <group><condition name="always" /></group>
                </condition>
              </group>
              <transition state="complete" />
            </condition>
            """);

        Assert.That(new TriggerState(trigger, null!).OnTick()?.Name, Is.EqualTo("complete"));
    }

    [Test]
    public void UnknownNestedPredicateCannotBecomeAnEmptySuccessfulAllOf() {
        TriggerDefinition trigger = Parse("""
            <condition name="all_of">
              <group><condition name="unsupported_predicate" /></group>
              <transition state="complete" />
            </condition>
            """);

        Assert.That(trigger.States[0].Conditions, Is.Empty);
        Assert.That(new TriggerState(trigger, null!).OnTick(), Is.Null);
    }

    [Test]
    public void MissingGroupBodyCannotAdvanceTheState() {
        TriggerDefinition trigger = Parse("""
            <condition name="all_of">
              <transition state="complete" />
            </condition>
            """);

        Assert.That(trigger.States[0].Conditions, Is.Empty);
        Assert.That(new TriggerState(trigger, null!).OnTick(), Is.Null);
    }

    [Test]
    public void NoncanonicalGroupEffectsAreNotSilentlyDiscarded() {
        TriggerDefinition trigger = Parse("""
            <condition name="all_of">
              <group>
                <condition name="always" />
                <action name="set_user_value" key="unlocked" value="1" trigger_id="1" />
              </group>
              <transition state="complete" />
            </condition>
            """);

        Assert.That(trigger.States[0].Conditions, Is.Empty);
    }

    private static TriggerDefinition Parse(string condition) {
        var xml = new XmlDocument();
        xml.LoadXml($"<ms2><state name=\"start\">{condition}</state><state name=\"complete\" /></ms2>");
        return TriggerCache.ParseTrigger("test_map", "test_trigger", xml);
    }

    private sealed class RecordingAction(Action action) : IAction {
        public void Execute(TriggerContext context) => action();
    }
}
