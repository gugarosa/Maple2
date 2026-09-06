using Maple2.Model.Enum;
using Maple2.Model.Game;
using Maple2.Model.Metadata;
using Maple2.Server.Core.Formulas;

namespace Maple2.Server.Game.Model;

public class Stats {
    public const int BASIC_TOTAL = 35;
    public const int SPECIAL_TOTAL = 180;

    private readonly Dictionary<BasicAttribute, Stat> basicValues;
    private readonly Dictionary<SpecialAttribute, Stat> specialValues;

    public int GearScore = 0;

    public Stats(IReadOnlyDictionary<BasicAttribute, long> statsDictionary, JobCode jobCode) {
        basicValues = new Dictionary<BasicAttribute, Stat>();
        specialValues = new Dictionary<SpecialAttribute, Stat>();
        Reset(statsDictionary, jobCode);
    }

    public Stats(NpcMetadataStat npcStats) {
        basicValues = new Dictionary<BasicAttribute, Stat>();
        specialValues = new Dictionary<SpecialAttribute, Stat>();
        foreach ((BasicAttribute attribute, long value) in npcStats.Stats) {
            this[attribute].AddBase(value);
        }
    }

    /// <summary>
    /// Clears and sets static stats.
    /// </summary>
    public void SetStaticStats(IReadOnlyDictionary<BasicAttribute, long> statsDictionary) {
        ClearStats();
        foreach (BasicAttribute attribute in statsDictionary.Keys) {
            this[attribute].AddBase(statsDictionary[attribute]);
        }
        // Does not add PhysicalAtk or MagicalAtk due to it not using jobCode
    }

    public void Reset(IReadOnlyDictionary<BasicAttribute, long> metadata, JobCode jobCode) {
        ClearStats();

        foreach (BasicAttribute attribute in metadata.Keys) {
            if (attribute is BasicAttribute.PhysicalAtk or BasicAttribute.MagicalAtk) {
                continue;
            }
            this[attribute].AddBase(metadata[attribute]);
        }

        // TODO: Remove when UserStat has rmsp
        this[BasicAttribute.MountSpeed].AddBase(100);

        this[BasicAttribute.PhysicalAtk].AddBase(AttackStat.PhysicalAtk(jobCode, this[BasicAttribute.Strength].Base, this[BasicAttribute.Dexterity].Base, this[BasicAttribute.Luck].Base));
        this[BasicAttribute.MagicalAtk].AddBase(AttackStat.MagicalAtk(jobCode, this[BasicAttribute.Intelligence].Base));
    }

    /// <summary>
    ///  Apply rate bonus to total value of each stat
    /// </summary>
    public void Total() {
        foreach (Stat stat in basicValues.Values) {
            long rateBonus = (long) (stat.Rate * (stat.Base + (stat.Total - stat.Base)));
            stat.AddTotal(rateBonus);
        }

        foreach (Stat stat in specialValues.Values) {
            long rateBonus = (long) (stat.Rate * (stat.Base + (stat.Total - stat.Base)));
            stat.AddTotal(rateBonus);
        }
    }

    public Stat this[BasicAttribute attribute] {
        get {
            if (!basicValues.ContainsKey(attribute)) {
                basicValues[attribute] = new Stat(0, 0, 0);
            }

            return basicValues[attribute];
        }
        set => basicValues[attribute] = value;
    }

    public Stat this[SpecialAttribute attribute] {
        get {
            if (!specialValues.ContainsKey(attribute)) {
                specialValues[attribute] = new Stat(0, 0, 0);
            }

            return specialValues[attribute];
        }
        set => specialValues[attribute] = value;
    }

    /// <summary>
    /// Safely clear stats.
    /// </summary>
    private void ClearStats() {
        foreach (Stat stat in basicValues.Values) {
            stat.AddBase(-stat.Base);
            stat.AddTotal(-stat.Total);
            stat.AddRate(-stat.Rate);
        }
        foreach (Stat stat in specialValues.Values) {
            stat.AddBase(-stat.Base);
            stat.AddTotal(-stat.Total);
            stat.AddRate(-stat.Rate);
        }
    }
}

public sealed class Stat {
    public const int TOTAL = 3;

    public long Total { get; set; }
    public long Base { get; set; }
    public long Current { get; set; }
    public float Rate { get; set; }

    public Stat(long value) : this(value, value, value) { }

    public Stat(long total, long @base, long current) {
        Total = total;
        Base = @base;
        Current = current;
    }

    public void AddBase(long amount) {
        Total = Math.Max(0, Total + amount);
        Base = Math.Max(0, Base + amount);
        Current = Math.Max(0, Current + amount);
    }

    public void AddTotal(long amount) {
        Total = Math.Max(0, Total + amount);
        Current = Math.Max(0, Current + amount);
    }

    public void AddTotal(BasicOption option) {
        AddTotal(option.Value);
        Rate += option.Rate;
    }

    public void AddTotal(SpecialOption option) {
        AddTotal((int) option.Value);
        Rate += option.Rate;
    }

    public void AddRate(float rate) {
        Rate += rate;
    }

    public void Add(long amount) {
        Current = Math.Clamp(Current + amount, 0, Total);
    }

    public long this[int i] {
        get {
            return i switch {
                0 => Total,
                1 => Base,
                _ => Current,
            };
        }
    }

    public double Multiplier() => Total / 1000.0;

    public override string ToString() => $"<{Total}|{Base}|{Current}>";
}
