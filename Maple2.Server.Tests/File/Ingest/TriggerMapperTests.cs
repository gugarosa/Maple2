using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using Maple2.File.Ingest.Mapper;
using Maple2.Model.Enum;
using Maple2.Server.Game.Trigger;
using Maple2.Server.Game.Trigger.Helpers;
using TriggerDefinition = Maple2.Server.Game.Trigger.Helpers.Trigger;

namespace Maple2.Server.Tests.File.Ingest;

public class TriggerMapperTests {
    [Test]
    public void ActionRenameDoesNotRequireAFunctionSplitter() {
        var document = new XmlDocument();
        document.LoadXml("""
            <ms2><state name="start"><onEnter>
              <action name="DungeonVariable" varID="2" value="1" />
            </onEnter></state></ms2>
            """);

        document.LoadXml(TriggerMapper.NormalizeTriggerXmlNames(document));
        var action = (XmlElement) document.SelectSingleNode("//action")!;
        Assert.That(action.GetAttribute("name"), Is.EqualTo("set_dungeon_variable"));
        Assert.That(action.GetAttribute("var_id"), Is.EqualTo("2"));
        Assert.That(TriggerFunctionMapping.ActionMap[action.GetAttribute("name")](action.Attributes),
            Is.TypeOf<TriggerDefinition.SetDungeonVariable>());
    }

    [TestCase("AddCinematicTalk", "align", "Reft", "left")]
    [TestCase("CreateFieldGame", "type", "MapleSurvive", "MapleSurvival")]
    public void KnownSourceEnumSpellingsAreNormalizedBeforeRuntime(
        string actionName, string attribute, string source, string expected) {
        var document = new XmlDocument();
        document.LoadXml($"""
            <ms2><state name="start"><onEnter>
              <action name="{actionName}" {attribute}="{source}" />
            </onEnter></state></ms2>
            """);

        document.LoadXml(TriggerMapper.NormalizeTriggerXmlNames(document));
        var action = (XmlElement) document.SelectSingleNode("//action")!;
        Assert.That(action.GetAttribute(attribute), Is.EqualTo(expected));
    }

    [TestCase("UserDetected", true)]
    [TestCase("!UserDetected", false)]
    public void NegatedSourceBoxSelectorUsesCanonicalConditionNegation(string name, bool negate) {
        var document = new XmlDocument();
        document.LoadXml($"""
            <ms2><state name="start">
              <condition name="{name}" arg1="!100"><transition state="complete" /></condition>
            </state><state name="complete" /></ms2>
            """);

        document.LoadXml(TriggerMapper.NormalizeTriggerXmlNames(document));
        XmlElement condition = (XmlElement) document.SelectSingleNode("//condition")!;
        Assert.That(condition.GetAttribute("name"), Is.EqualTo("user_detected"));
        Assert.That(condition.GetAttribute("box_ids"), Is.EqualTo("100"));
        Assert.That(condition.GetAttribute("negate"), Is.EqualTo(negate ? "true" : "false"));
    }

    [Test]
    public void GroupActionsArePromotedToCanonicalConditionBodyInOrder() {
        var document = new XmlDocument();
        document.LoadXml("""
            <ms2>
              <state name="start">
                <condition name="AllOf">
                  <group>
                    <condition name="True" />
                    <action name="SetUserValue" key="first" value="1" triggerId="1" />
                    <action name="SetUserValue" key="second" value="1" triggerId="1" />
                  </group>
                  <action name="SetUserValue" key="third" value="1" triggerId="1" />
                  <transition state="complete" />
                </condition>
              </state>
              <state name="complete" />
            </ms2>
            """);

        string normalized = TriggerMapper.NormalizeTriggerXmlNames(document);
        document.LoadXml(normalized);
        XmlNode condition = document.SelectSingleNode("//condition[@name='all_of']")!;
        Assert.That(condition.SelectNodes("group/action"), Is.Empty);
        Assert.That(condition.SelectNodes("action")!.Cast<XmlNode>()
            .Select(action => action.Attributes!["key"]!.Value),
            Is.EqualTo(new[] { "first", "second", "third" }));
        Assert.That(condition.SelectSingleNode("transition")?.Attributes?["state"]?.Value,
            Is.EqualTo("complete"));
    }

    private const string WakeupTrigger = """
        <ms2>
          <state name="idle">
            <condition name="quest_user_detected" box_ids="9900" quest_ids="60100005-60100010" quest_states="2-2">
              <transition state="ready" />
            </condition>
          </state>
          <state name="ready" />
          <state name="fadein">
            <onEnter>
              <action name="spawn_monster" spawn_ids="103" />
            </onEnter>
          </state>
          <state name="warp" />
        </ms2>
        """;

    [Test]
    public void CompletedPreviousQuestAndAbsentNextQuestSpawnsFrey() {
        XmlDocument trigger = Transform();
        Dictionary<int, (QuestState State, bool CanComplete)> quests = new() {
            [60100005] = (QuestState.Completed, false),
        };

        Assert.That(NextState(trigger, "idle", quests), Is.EqualTo("FreyRecoveryCheck"));
        Assert.That(NextState(trigger, "FreyRecoveryCheck", quests), Is.EqualTo("FreyRecoverySpawn"));
    }

    [Test]
    public void StartedNextQuestContinuesToWarp() {
        XmlDocument trigger = Transform();
        Dictionary<int, (QuestState State, bool CanComplete)> quests = new() {
            [60100005] = (QuestState.Completed, false),
            [60100010] = (QuestState.Started, false),
        };

        Assert.That(NextState(trigger, "idle", quests), Is.EqualTo("FreyRecoveryCheck"));
        Assert.That(NextState(trigger, "FreyRecoveryCheck", quests), Is.EqualTo("warp"));
    }

    [Test]
    public void ReadyNextQuestUsesOriginalReadyState() {
        XmlDocument trigger = Transform();
        Dictionary<int, (QuestState State, bool CanComplete)> quests = new() {
            [60100005] = (QuestState.Completed, false),
            [60100010] = (QuestState.Started, true),
        };

        Assert.That(NextState(trigger, "idle", quests), Is.EqualTo("ready"));
    }

    [Test]
    public void CompletedNextQuestStopsRecovery() {
        XmlDocument trigger = Transform();
        Dictionary<int, (QuestState State, bool CanComplete)> quests = new() {
            [60100005] = (QuestState.Completed, false),
            [60100010] = (QuestState.Completed, false),
        };

        Assert.That(NextState(trigger, "idle", quests), Is.EqualTo("FreyRecoveryCheck"));
        Assert.That(NextState(trigger, "FreyRecoveryCheck", quests), Is.EqualTo("FreyRecoveryDone"));
    }

    [Test]
    public void RecoveredFreyWarpsAfterOfferingNextQuest() {
        XmlDocument trigger = Transform();
        Dictionary<int, (QuestState State, bool CanComplete)> quests = new() {
            [60100005] = (QuestState.Completed, false),
        };
        Assert.That(NextState(trigger, "FreyRecoverySpawn", quests), Is.Null);

        quests[60100010] = (QuestState.Started, false);
        Assert.That(NextState(trigger, "FreyRecoverySpawn", quests), Is.EqualTo("warp"));

        quests[60100010] = (QuestState.Started, true);
        Assert.That(NextState(trigger, "FreyRecoverySpawn", quests), Is.EqualTo("ready"));

        quests[60100010] = (QuestState.Completed, false);
        Assert.That(NextState(trigger, "FreyRecoverySpawn", quests), Is.EqualTo("FreyRecoveryDone"));
    }

    [Test]
    public void TransformIsIdempotent() {
        string once = TriggerMapper.ApplyKnownProgressionFixes("63000042_cs", "wakeup02", WakeupTrigger);
        string twice = TriggerMapper.ApplyKnownProgressionFixes("63000042_cs", "wakeup02", once);

        Assert.That(twice, Is.EqualTo(once));
    }

    [Test]
    public void UnrelatedTriggerIsUnchanged() {
        const string unrelated = "<ms2><state name=\"idle\" /></ms2>";

        Assert.That(TriggerMapper.ApplyKnownProgressionFixes("other_map", "wakeup02", unrelated), Is.EqualTo(unrelated));
    }

    [Test]
    public void TargetedTriggerWithUnexpectedShapeIsRejected() {
        Assert.That(
            () => TriggerMapper.ApplyKnownProgressionFixes("63000042_cs", "wakeup02", "<ms2><state name=\"other\" /></ms2>"),
            Throws.TypeOf<InvalidDataException>());
    }

    private static XmlDocument Transform() {
        string xml = TriggerMapper.ApplyKnownProgressionFixes("63000042_cs", "wakeup02", WakeupTrigger);
        var document = new XmlDocument();
        document.LoadXml(xml);
        return document;
    }

    private static string? NextState(XmlDocument trigger, string stateName, IReadOnlyDictionary<int, (QuestState State, bool CanComplete)> quests) {
        XmlNode? state = trigger.SelectSingleNode($"//state[@name='{stateName}']");
        Assert.That(state, Is.Not.Null);

        foreach (XmlNode condition in state!.SelectNodes("condition")!) {
            string conditionName = condition.Attributes!["name"]!.Value;
            bool matches;
            if (conditionName == "quest_user_detected") {
                int[] questIds = TriggerFunctionMapping.ParseIntArray(condition.Attributes["quest_ids"]?.Value);
                int[] questStates = TriggerFunctionMapping.ParseIntArray(condition.Attributes["quest_states"]?.Value);
                bool detected = questIds.Any(questId =>
                    quests.TryGetValue(questId, out var quest) &&
                    TriggerContext.MatchesQuestState(quest.State, quest.CanComplete, questStates));
                bool negate = condition.Attributes["negate"]?.Value == "true";
                matches = TriggerContext.ApplyNegate(detected, negate);
            } else {
                matches = conditionName == "always";
            }

            if (matches) {
                return condition.SelectSingleNode("transition")?.Attributes?["state"]?.Value;
            }
        }

        return null;
    }
}
