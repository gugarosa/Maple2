using Maple2.Model.Enum;

namespace Maple2.Model.Metadata;

public record GuildTable(
    IReadOnlyDictionary<int, IReadOnlyDictionary<short, GuildTable.Buff>> Buffs,
    IReadOnlyDictionary<int, IReadOnlyDictionary<int, GuildTable.House>> Houses,
    IReadOnlyDictionary<GuildNpcType, IReadOnlyDictionary<short, GuildTable.Npc>> Npcs,
    IReadOnlyDictionary<short, GuildTable.Property> Properties) : Table {

    public Property GetProperty(int experience) {
        return Properties.Values
            .Where(property => property.Experience <= experience)
            .MaxBy(property => property.Experience)
            ?? Properties.Values.MinBy(property => property.Experience)
            ?? throw new InvalidOperationException("Guild properties are empty.");
    }

    public (int Experience, int Funds) AddProgress(int experience, int funds, int addExperience, int addFunds) {
        ArgumentOutOfRangeException.ThrowIfNegative(experience);
        ArgumentOutOfRangeException.ThrowIfNegative(funds);
        ArgumentOutOfRangeException.ThrowIfNegative(addExperience);
        ArgumentOutOfRangeException.ThrowIfNegative(addFunds);
        int updatedExperience = (int) Math.Min((long) experience + addExperience, int.MaxValue);
        long fundMax = GetProperty(updatedExperience).FundMax;
        if (fundMax < 0) {
            throw new InvalidDataException("Guild fund capacity cannot be negative.");
        }
        int updatedFunds = (int) Math.Max(funds,
            Math.Min(Math.Min(fundMax, int.MaxValue), (long) funds + addFunds));
        return (updatedExperience, updatedFunds);
    }

    public record Buff(
        int Id,
        short Level,
        short RequireLevel,
        int Cost,
        int UpgradeCost,
        int Duration);

    public record House(
        int MapId,
        int RequireLevel,
        int UpgradeCost,
        int ReThemeCost,
        int[] Facilities);

    public record Npc(
        GuildNpcType Type,
        short Level,
        int RequireGuildLevel,
        int RequireHouseLevel,
        int UpgradeCost);

    public record Property(
        short Level,
        int Experience,
        byte Capacity,
        long FundMax,
        int DonateMax,
        int CheckInExp,
        int WinMiniGameExp,
        int LoseMiniGameExp,
        int RaidExp,
        int CheckInFund,
        int WinMiniGameFund,
        int LoseMiniGameFund,
        int RaidFund,
        float CheckInPlayerExpRate,
        float DonatePlayerExpRate,
        int CheckInCoin,
        int DonateCoin,
        int WinMiniGameCoin,
        int LoseMiniGameCoin);
}
