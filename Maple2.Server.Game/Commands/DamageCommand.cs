using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.IO;
using System.Numerics;
using Maple2.Model.Enum;
using Maple2.Server.Game.Manager;
using Maple2.Server.Game.Model;
using Maple2.Server.Game.Session;

namespace Maple2.Server.Game.Commands;

public class DamageCommand : GameCommand {
    private readonly GameSession session;

    public DamageCommand(GameSession session)
        : base(AdminPermissions.None, "damage", "Measure observed damage against one NPC or training dummy.") {
        this.session = session;
        var target = new Argument<int?>("object-id", () => null, "Target object ID; defaults to the nearest living NPC.");
        var start = new Command("start", "Start a new measurement window.");
        start.AddArgument(target);
        start.SetHandler<InvocationContext, int?>(Start, target);
        AddCommand(start);

        var stop = new Command("stop", "Stop measuring and show the results.");
        stop.SetHandler<InvocationContext>(Stop);
        AddCommand(stop);

        var show = new Command("show", "Show the current measurement.");
        show.SetHandler<InvocationContext>(Show);
        AddCommand(show);
        this.SetHandler<InvocationContext>(Show);
    }

    private void Start(InvocationContext context, int? objectId) {
        if (session.Field == null) {
            context.Console.Error.WriteLine("No field loaded.");
            return;
        }

        FieldNpc? npc = objectId.HasValue
            ? session.Field.EnumerateNpcs().FirstOrDefault(candidate => candidate.ObjectId == objectId.Value)
            : session.Field.EnumerateNpcs()
                .Where(candidate => !candidate.IsDead)
                .MinBy(candidate => Vector3.DistanceSquared(session.Player.Position, candidate.Position));
        if (npc == null || npc.IsDead) {
            context.Console.Error.WriteLine("No living NPC found for that target.");
            return;
        }

        session.DamageTracking = new DamageTracking(session.Field.RoomId, npc.ObjectId, Environment.TickCount64);
        context.Console.Out.WriteLine($"Measuring your skill, pet and DoT damage to {npc.Value.Metadata.Name} ({npc.ObjectId}). Use 'damage stop' to finish.");
    }

    private void Stop(InvocationContext context) {
        session.DamageTracking?.Stop(Environment.TickCount64);
        Show(context);
    }

    private void Show(InvocationContext context) {
        if (session.DamageTracking == null) {
            context.Console.Error.WriteLine("Start a measurement with 'damage start [object-id]'.");
            return;
        }

        DamageSummary summary = session.DamageTracking.Snapshot(Environment.TickCount64);
        context.Console.Out.WriteLine($"Target {summary.TargetId}: {(summary.Running ? "measuring" : "stopped")}, {summary.Seconds:F2}s");
        context.Console.Out.WriteLine($"Observed total {summary.Total:N0}, DPS {summary.Dps:N2}, min/max hit {summary.Minimum:N0}/{summary.Maximum:N0}");
        context.Console.Out.WriteLine($"Hits {summary.Hits:N0}, critical {summary.CriticalHits:N0}, missed {summary.Misses:N0}, blocked {summary.Blocks:N0}");
    }
}
