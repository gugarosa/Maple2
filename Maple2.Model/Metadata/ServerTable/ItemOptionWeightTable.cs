using Maple2.Model.Enum;
using Maple2.Model.Game;

namespace Maple2.Model.Metadata;

public record ItemOptionWeightTable(
    IReadOnlyDictionary<BasicAttribute, ItemOptionProbabilityMetadata> BasicProbabilities,
    IReadOnlyDictionary<SpecialAttribute, ItemOptionProbabilityMetadata> SpecialProbabilities,
    IReadOnlyDictionary<int, ItemOptionWeight[]> Options
) : ServerTable {
    public int GetWeight(in ItemType itemType, BasicAttribute attribute) {
        return BasicProbabilities.TryGetValue(attribute, out ItemOptionProbabilityMetadata? probability)
            ? probability.GetWeight(itemType)
            : 0;
    }

    public int GetWeight(in ItemType itemType, SpecialAttribute attribute) {
        return SpecialProbabilities.TryGetValue(attribute, out ItemOptionProbabilityMetadata? probability)
            ? probability.GetWeight(itemType)
            : 0;
    }
}

public record ItemOptionProbabilityMetadata(int Weapon, int Armor, int Accessory, int Pet) {
    public int GetWeight(in ItemType itemType) {
        if (itemType.IsWeapon) return Weapon;
        if (itemType.IsArmor) return Armor;
        if (itemType.IsAccessory) return Accessory;
        if (itemType.IsPet) return Pet;
        return 0;
    }
}

public record ItemOptionWeight(
    BasicAttribute? BasicAttribute,
    SpecialAttribute? SpecialAttribute,
    int Weight,
    bool IsRate,
    ItemOptionWeightedValue[] Values
);

public readonly record struct ItemOptionWeightedValue(int Value, int Weight);
