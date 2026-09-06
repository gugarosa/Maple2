namespace Maple2.Model.Metadata;

public record DungeonRoundTable(IReadOnlyDictionary<int, DungeonRoundTable.Entry> Entries) : Table {
    public record Entry(
        int Id,
        Round[] Rounds
    );

    public record Round(
        int Number,
        int RewardId,
        int GearScore
    );
}
