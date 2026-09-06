using Maple2.Model.Enum;
using Maple2.Model.Metadata;
using Maple2.PacketLib.Tools;
using Maple2.Tools;
using Maple2.Tools.Extensions;

namespace Maple2.Server.Game.Manager.Config;

public class StatAttributes : IByteSerializable {
    public readonly PointSources Sources;
    public readonly PointAllocation Allocation;

    public int TotalPoints => Sources.Count;
    public int UsedPoints => Allocation.Count;

    public StatAttributes(ConstantsTable constants) {
        Sources = new PointSources();
        Allocation = new PointAllocation(constants);
    }

    public void WriteTo(IByteWriter writer) {
        writer.WriteClass<PointSources>(Sources);
        writer.WriteClass<PointAllocation>(Allocation);
    }

    public class PointSources : IByteSerializable {
        // MaxPoints - Trophy:38, Exploration:12, Prestige:50
        public readonly IDictionary<AttributePointSource, int> Points;

        public int Count => Points.Values.Sum();

        public int this[AttributePointSource type] {
            get => Points[type];
            set => Points[type] = value;
        }

        public PointSources() {
            Points = new Dictionary<AttributePointSource, int>();
            foreach (AttributePointSource source in Enum.GetValues(typeof(AttributePointSource))) {
                Points[source] = 0;
            }
        }

        public void WriteTo(IByteWriter writer) {
            writer.WriteInt(Points.Count);
            foreach ((AttributePointSource source, int amount) in Points) {
                writer.Write<AttributePointSource>(source);
                writer.WriteInt(amount);
            }
        }
    }

    public class PointAllocation : IByteSerializable {
        private readonly Dictionary<BasicAttribute, int> points;
        private readonly ConstantsTable constants;

        public BasicAttribute[] Attributes => points.Keys.ToArray();
        public int Count => points.Values.Sum();

        public int this[BasicAttribute type] {
            get => points.GetValueOrDefault(type);
            set {
                if (value < 0 || value > StatLimit(type, constants)) {
                    return;
                }
                if (value == 0) {
                    points.Remove(type);
                    return;
                }

                points[type] = value;
            }
        }

        public PointAllocation(ConstantsTable constants) {
            points = new Dictionary<BasicAttribute, int>();
            this.constants = constants;
        }

        public static int StatLimit(BasicAttribute type, ConstantsTable constants) {
            return type switch {
                BasicAttribute.Strength => constants.StatPointLimit_str,
                BasicAttribute.Dexterity => constants.StatPointLimit_dex,
                BasicAttribute.Intelligence => constants.StatPointLimit_int,
                BasicAttribute.Luck => constants.StatPointLimit_luk,
                BasicAttribute.Health => constants.StatPointLimit_hp,
                BasicAttribute.CriticalRate => constants.StatPointLimit_cap,
                _ => 0,
            };
        }

        public void WriteTo(IByteWriter writer) {
            writer.WriteInt(points.Count);
            foreach ((BasicAttribute type, int value) in points) {
                writer.Write<BasicAttribute>(type);
                writer.WriteInt(value);
            }
        }
    }
}
