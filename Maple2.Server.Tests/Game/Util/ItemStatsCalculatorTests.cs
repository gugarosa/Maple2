using System;
using System.Collections.Generic;
using Maple2.Model.Enum;
using Maple2.Model.Game;
using Maple2.Model.Metadata;
using Maple2.Server.Game.Util;

namespace Maple2.Server.Tests.Game.Util;

[TestFixture]
public class ItemStatsCalculatorTests {
    private static readonly ItemOption Option = new(
        MultiplyFactor: 1,
        NumPick: new ItemOption.Range<int>(1, 2),
        Entries: [
            new ItemOption.Entry(BasicAttribute: BasicAttribute.Health, Values: new ItemOption.Range<int>(1, 1)),
            new ItemOption.Entry(BasicAttribute: BasicAttribute.Defense, Values: new ItemOption.Range<int>(1, 1)),
        ]);

    [Test]
    public void RandomItemOption_UsesItemCategoryWeight() {
        var weights = new ItemOptionWeightTable(
            new Dictionary<BasicAttribute, ItemOptionProbabilityMetadata> {
                [BasicAttribute.Health] = new(Weapon: 100, Armor: 1, Accessory: 1, Pet: 1),
                [BasicAttribute.Defense] = new(Weapon: 1, Armor: 100, Accessory: 1, Pet: 1),
            },
            new Dictionary<SpecialAttribute, ItemOptionProbabilityMetadata>(),
            new Dictionary<int, ItemOptionWeight[]>());

        ItemStats.Option weapon = ItemStatsCalculator.RandomItemOption(
            1, Option, new ItemType(1, 30), ItemStats.Type.Random, weights, count: 1,
            random: new FixedRandom(50));
        ItemStats.Option armor = ItemStatsCalculator.RandomItemOption(
            1, Option, new ItemType(1, 13), ItemStats.Type.Random, weights, count: 1,
            random: new FixedRandom(50));

        Assert.Multiple(() => {
            Assert.That(weapon.Basic.Keys, Is.EqualTo(new[] { BasicAttribute.Health }));
            Assert.That(armor.Basic.Keys, Is.EqualTo(new[] { BasicAttribute.Defense }));
        });
    }

    [Test]
    public void RandomItemOption_DoesNotSelectExplicitZeroWeightOptions() {
        var weights = new ItemOptionWeightTable(
            new Dictionary<BasicAttribute, ItemOptionProbabilityMetadata> {
                [BasicAttribute.Health] = new(Weapon: 0, Armor: 0, Accessory: 0, Pet: 0),
                [BasicAttribute.Defense] = new(Weapon: 0, Armor: 10, Accessory: 0, Pet: 0),
            },
            new Dictionary<SpecialAttribute, ItemOptionProbabilityMetadata>(),
            new Dictionary<int, ItemOptionWeight[]>());

        ItemStats.Option rolled = ItemStatsCalculator.RandomItemOption(
            1, Option, new ItemType(1, 13), ItemStats.Type.Random, weights, count: 2,
            random: new FixedRandom(0));

        Assert.Multiple(() => {
            Assert.That(rolled.Count, Is.EqualTo(1));
            Assert.That(rolled.Basic.Keys, Is.EqualTo(new[] { BasicAttribute.Defense }));
        });
    }

    [Test]
    public void RandomItemOption_StopsWhenAllCandidatesHaveExplicitZeroWeight() {
        var weights = new ItemOptionWeightTable(
            new Dictionary<BasicAttribute, ItemOptionProbabilityMetadata> {
                [BasicAttribute.Health] = new(Weapon: 0, Armor: 0, Accessory: 0, Pet: 0),
                [BasicAttribute.Defense] = new(Weapon: 0, Armor: 0, Accessory: 0, Pet: 0),
            },
            new Dictionary<SpecialAttribute, ItemOptionProbabilityMetadata>(),
            new Dictionary<int, ItemOptionWeight[]>());

        ItemStats.Option rolled = ItemStatsCalculator.RandomItemOption(
            1, Option, new ItemType(1, 13), ItemStats.Type.Random, weights, count: 1,
            random: new FixedRandom(0));

        Assert.That(rolled.Count, Is.Zero);
    }

    [Test]
    public void RandomItemOption_PreservesLockedAttributeWithoutDuplicates() {
        ItemStats.Option rolled = ItemStatsCalculator.RandomItemOption(
            1, Option, new ItemType(1, 13), ItemStats.Type.Random, count: 2,
            random: new FixedRandom(0), presets: [new LockOption(BasicAttribute.Health, true)]);
        var original = new ItemStats.Option(new Dictionary<BasicAttribute, BasicOption> {
            [BasicAttribute.Health] = new(777),
            [BasicAttribute.Defense] = new(2),
        });

        ItemStatsCalculator.RestoreLockedOptions(
            original, rolled, new LockOption(BasicAttribute.Health, true));

        Assert.Multiple(() => {
            Assert.That(rolled.Count, Is.EqualTo(2));
            Assert.That(rolled.Basic.Keys, Is.EquivalentTo(new[] {
                BasicAttribute.Health,
                BasicAttribute.Defense,
            }));
            Assert.That(rolled.Basic[BasicAttribute.Health].Value, Is.EqualTo(777));
        });
    }

    [Test]
    public void RandomItemOption_DoesNotCountDuplicateAttributesAsExtraLines() {
        var option = new ItemOption(
            MultiplyFactor: 1,
            NumPick: new ItemOption.Range<int>(2, 2),
            Entries: [
                new ItemOption.Entry(BasicAttribute: BasicAttribute.Health, Values: new ItemOption.Range<int>(1, 1)),
                new ItemOption.Entry(BasicAttribute: BasicAttribute.Health, Values: new ItemOption.Range<int>(2, 2)),
                new ItemOption.Entry(BasicAttribute: BasicAttribute.Defense, Values: new ItemOption.Range<int>(3, 3)),
            ]);

        ItemStats.Option rolled = ItemStatsCalculator.RandomItemOption(
            1, option, new ItemType(1, 13), ItemStats.Type.Random, count: 2,
            random: new FixedRandom(0));

        Assert.That(rolled.Basic.Keys, Is.EquivalentTo(new[] {
            BasicAttribute.Health,
            BasicAttribute.Defense,
        }));
    }

    [Test]
    public void ApplyWeightedValue_PreservesLockedValue() {
        var source = new ItemStats.Option(new Dictionary<BasicAttribute, BasicOption> {
            [BasicAttribute.Health] = new(777),
            [BasicAttribute.Defense] = new(2),
        });
        var target = new ItemStats.Option(new Dictionary<BasicAttribute, BasicOption> {
            [BasicAttribute.Health] = new(1),
            [BasicAttribute.Defense] = new(1),
        }, multiplyFactor: 2);
        var healthWeight = new ItemOptionWeight(
            BasicAttribute.Health, null, 1, false, [new(10, 1), new(20, 3)]);
        var defenseWeight = new ItemOptionWeight(
            BasicAttribute.Defense, null, 1, false, [new(30, 1)]);

        Assert.That(ItemStatsCalculator.ApplyWeightedValue(
            target, BasicAttribute.Health, null, healthWeight, random: new FixedRandom(1)), Is.True);
        Assert.That(ItemStatsCalculator.ApplyWeightedValue(
            target, BasicAttribute.Defense, null, defenseWeight, random: new FixedRandom(0)), Is.True);
        ItemStatsCalculator.RestoreLockedOptions(
            source, target, new LockOption(BasicAttribute.Health, true));

        Assert.Multiple(() => {
            Assert.That(target.Basic[BasicAttribute.Health].Value, Is.EqualTo(777));
            Assert.That(target.Basic[BasicAttribute.Defense].Value, Is.EqualTo(60));
        });
    }

    private sealed class FixedRandom(int value) : Random {
        public override int Next(int maxValue) => Math.Min(value, maxValue - 1);
        public override int Next(int minValue, int maxValue) => Math.Clamp(value, minValue, maxValue - 1);
        public override float NextSingle() => 0;
    }
}
