using System.Collections.Generic;
using Maple2.Model.Enum;
using Maple2.Model.Game;
using Maple2.Model.Metadata;
using Maple2.Server.Game.Manager;
using GameItem = Maple2.Model.Game.Item;

namespace Maple2.Server.Tests.Game.Manager.Item;

public class ItemEnchantTests {
    [Test]
    public void CumulativeEnchantSumsLevelsWithoutChangingExistingItem() {
        (GameItem item, EnchantOptionTable table) = CreateFixture();
        var original = new ItemEnchant(1, basicOptions: new Dictionary<BasicAttribute, BasicOption> {
            [BasicAttribute.MinWeaponAtk] = new BasicOption(0.9f),
        });
        item.Enchant = original;

        Assert.That(ItemEnchantManager.TryGetCumulativeEnchant(table, item, 2, out ItemEnchant? result), Is.True);
        Assert.Multiple(() => {
            Assert.That(result!.Enchants, Is.EqualTo(2));
            Assert.That(result.BasicOptions[BasicAttribute.MinWeaponAtk].Rate, Is.EqualTo(0.3f).Within(0.000001));
            Assert.That(result.BasicOptions[BasicAttribute.MaxWeaponAtk].Rate, Is.EqualTo(0.2f).Within(0.000001));
            Assert.That(item.Enchant, Is.SameAs(original));
            Assert.That(original.Enchants, Is.EqualTo(1));
            Assert.That(original.BasicOptions[BasicAttribute.MinWeaponAtk].Rate, Is.EqualTo(0.9f));
        });
    }

    [Test]
    public void ZeroEnchantHasNoBonus() {
        (GameItem item, EnchantOptionTable table) = CreateFixture();

        Assert.That(ItemEnchantManager.TryGetCumulativeEnchant(table, item, 0, out ItemEnchant? result), Is.True);
        Assert.That(result!.Enchants, Is.Zero);
        Assert.That(result.BasicOptions, Is.Empty);
    }

    [TestCase(-1)]
    [TestCase(16)]
    public void InvalidEnchantLevelFails(int target) {
        (GameItem item, EnchantOptionTable table) = CreateFixture();

        Assert.That(ItemEnchantManager.TryGetCumulativeEnchant(table, item, target, out ItemEnchant? result), Is.False);
        Assert.That(result, Is.Null);
    }

    [Test]
    public void MissingIntermediateLevelDoesNotProducePartialStats() {
        (GameItem item, EnchantOptionTable table) = CreateFixture();
        var incomplete = new EnchantOptionTable(new Dictionary<int, EnchantOptionMetadata> {
            [2] = table.Entries[2],
        });

        Assert.That(ItemEnchantManager.TryGetCumulativeEnchant(incomplete, item, 2, out ItemEnchant? result), Is.False);
        Assert.That(result, Is.Null);
    }

    private static (GameItem, EnchantOptionTable) CreateFixture() {
        var property = new ItemMetadataProperty(false, 0, 1, 18, 0, "", "", ItemTag.None,
            0, 0, 0, 0, 0, 0, 0, 0, 0, [], false, 0, false, [], [], [], 0, 0);
        var limit = new ItemMetadataLimit(Gender.All, 50, 0, 4,
            true, true, true, true, true, false, false, 0, [], []);
        var metadata = new ItemMetadata(13000000, "Test weapon", [], "", [], new ItemMetadataLife(0, 0),
            property, new ItemMetadataCustomize(0, 0), limit, null, null, [], null, null, null, null);
        var item = new GameItem(metadata, rarity: 4, initialize: false);
        var table = new EnchantOptionTable(new Dictionary<int, EnchantOptionMetadata> {
            [1] = new(1, item.Type.Type, 1, 4, 0.1f, 1, 99, [BasicAttribute.MinWeaponAtk]),
            [2] = new(2, item.Type.Type, 2, 4, 0.2f, 1, 99, [BasicAttribute.MinWeaponAtk, BasicAttribute.MaxWeaponAtk]),
        });
        return (item, table);
    }
}
