namespace Maple2.Model.Metadata;

public record RoomRandomTable(IReadOnlyDictionary<int, RandomRoomEntry> RandomEntries,
                              IReadOnlyDictionary<int, RoomEntry> Rooms) : ServerTable;

public record RandomRoomEntry(
    int Id,
    int Probability,
    IReadOnlyDictionary<int, int> RoomWeights);

public record RoomEntry(
    int Id,
    int[] MapIds,
    int DurationTick,
    string AssetName,
    int MaxUserCount,
    bool AutoClose);
