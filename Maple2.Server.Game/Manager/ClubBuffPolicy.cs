using Maple2.Model.Enum;
using Maple2.Model.Metadata;

namespace Maple2.Server.Game.Manager;

internal static class ClubBuffPolicy {
    internal readonly record struct ClubSelection(
        ClubState State,
        int BuffId,
        IReadOnlyCollection<long> MemberIds);
    internal readonly record struct Effect(int Id, short Level);
    internal readonly record struct Changes(IReadOnlyList<Effect> Add, IReadOnlyList<int> Remove);

    internal static Dictionary<int, short> SelectEffects(
        IEnumerable<ClubSelection> clubs,
        long characterId,
        IReadOnlySet<long> fieldCharacterIds,
        IReadOnlyDictionary<int, ClubBuffTable.Entry> metadata) {
        var result = new Dictionary<int, short>();
        if (!fieldCharacterIds.Contains(characterId)) {
            return result;
        }
        foreach ((ClubState state, int buffId, IReadOnlyCollection<long> memberIds) in clubs) {
            if (state != ClubState.Established ||
                !memberIds.Contains(characterId) ||
                !memberIds.Any(id => id != characterId && fieldCharacterIds.Contains(id)) ||
                !metadata.TryGetValue(buffId, out ClubBuffTable.Entry? entry)) {
                continue;
            }

            result[entry.EffectId] = entry.EffectLevel;
        }
        return result;
    }

    internal static Changes GetChanges(
        IReadOnlyDictionary<int, short> active,
        IReadOnlyDictionary<int, short> desired) {
        List<int> remove = active
            .Where(pair => !desired.TryGetValue(pair.Key, out short level) || level != pair.Value)
            .Select(pair => pair.Key)
            .ToList();
        List<Effect> add = desired
            .Where(pair => !active.TryGetValue(pair.Key, out short level) || level != pair.Value)
            .Select(pair => new Effect(pair.Key, pair.Value))
            .ToList();
        return new Changes(add, remove);
    }
}
