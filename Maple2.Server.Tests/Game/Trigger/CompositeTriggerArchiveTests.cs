using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using Maple2.File.Ingest.Mapper;
using Maple2.File.IO;
using Maple2.File.Parser.Tools;
using Maple2.Server.Game.Trigger.Helpers;
using Maple2.Server.Game.Util;
using TriggerDefinition = Maple2.Server.Game.Trigger.Helpers.Trigger;

namespace Maple2.Server.Tests.Game.Trigger;

[Explicit("Requires a compatible client Xml.m2d under MS2_DATA_FOLDER; reads assets without changing them.")]
public class CompositeTriggerArchiveTests {
    [Test]
    public void ClientTriggersParseAndGroupsRetainPredicatesEffectsAndTransitions() {
        string? folder = Environment.GetEnvironmentVariable("MS2_DATA_FOLDER");
        if (string.IsNullOrWhiteSpace(folder)) {
            Assert.Ignore("Set MS2_DATA_FOLDER to run the read-only client archive regression.");
        }

        using var reader = new M2dReader(Path.Combine(folder!, "Xml.m2d"));
        Filter.Load(reader, "NA", "Live");
        var mapper = new TriggerMapper(reader);
        mapper.Process();
        int groupsChecked = 0;
        int scriptsChecked = 0;
        var parseErrors = new List<string>();

        foreach (var metadata in mapper.Results) {
            var document = new XmlDocument();
            document.LoadXml(metadata.Xml);
            foreach (XmlElement operation in document.SelectNodes("//action|//condition")!) {
                string name = operation.GetAttribute("name");
                bool mapped = operation.LocalName == "action"
                    ? TriggerFunctionMapping.ActionMap.ContainsKey(name)
                    : TriggerFunctionMapping.ConditionMap.ContainsKey(name);
                Assert.That(mapped, Is.True,
                    $"{metadata.MapXBlock}/{metadata.Name}: unmapped {operation.LocalName} {name}");
            }
            TriggerDefinition trigger;
            try {
                trigger = TriggerCache.ParseTrigger(metadata.MapXBlock, metadata.Name, document);
            } catch (Exception error) when (error is ArgumentException or FormatException or OverflowException) {
                parseErrors.Add($"{metadata.MapXBlock}/{metadata.Name}: {error.Message}");
                continue;
            }
            if (document.SelectNodes("//condition/group")!.Count == 0) {
                continue;
            }
            foreach (XmlNode state in document.SelectNodes("/ms2/state")!) {
                XmlNode[] sourceGroups = state.SelectNodes(
                    "condition[@name='any_one' or @name='all_of']")!.Cast<XmlNode>().ToArray();
                if (sourceGroups.Length == 0) {
                    continue;
                }
                string stateName = state.Attributes!["name"]!.Value;
                IGroupCondition[] parsedGroups = trigger.States.Single(candidate => candidate.Name == stateName)
                    .Conditions.OfType<IGroupCondition>().ToArray();
                string location = $"{metadata.MapXBlock}/{metadata.Name}:{stateName}";
                Assert.That(parsedGroups, Has.Length.EqualTo(sourceGroups.Length), location);

                for (int i = 0; i < sourceGroups.Length; i++) {
                    Assert.Multiple(() => {
                        Assert.That(parsedGroups[i].Conditions.Count,
                            Is.EqualTo(sourceGroups[i].SelectNodes("group/condition")!.Count), location);
                        Assert.That(parsedGroups[i].Actions.Count,
                            Is.EqualTo(sourceGroups[i].SelectNodes("action")!.Count), location);
                        Assert.That(parsedGroups[i].NextState,
                            Is.EqualTo(sourceGroups[i].SelectSingleNode("transition")?.Attributes?["state"]?.Value), location);
                    });
                    groupsChecked++;
                }
            }
            scriptsChecked++;
        }

        Assert.That(parseErrors, Is.Empty, string.Join(Environment.NewLine, parseErrors));
        Assert.That(groupsChecked, Is.GreaterThan(0));
        TestContext.Out.WriteLine(
            $"Parsed {mapper.Results.Count} trigger definitions; validated {groupsChecked} composite groups across {scriptsChecked} scripts.");
    }
}
