namespace Maple2.Model.Metadata;

public record ClubBuffTable(
    IReadOnlyDictionary<int, ClubBuffTable.Entry> Entries) : Table {

    public bool IsValidSelection(int buffId, int buffLevel) {
        return Entries.TryGetValue(buffId, out Entry? buff) && buff.EffectLevel == buffLevel;
    }

    public record Entry(
        int EffectId,
        short EffectLevel);
}
