using Maple2.Model.Enum;
using Maple2.Model.Error;
using Maple2.Model.Metadata;
using Maple2.Server.Game.Manager.Field;
using Maple2.Server.Game.Packets;
using Serilog;

namespace Maple2.Server.Game.Model;

public class RoomTimer : IUpdatable {
    private readonly FieldManager field;
    public int StartTick { get; private set; }
    public readonly RoomTimerType Type;
    public int Duration;
    private bool started;

    private readonly ILogger logger = Log.Logger.ForContext<RoomTimer>();

    public RoomTimer(FieldManager field, RoomTimerType type, int duration) {
        this.field = field;
        Type = type;
        Duration = duration;
    }

    public void Modify(int tick) {
        int originalDuration = Duration;
        Duration = Math.Max(0, Duration + tick);
        int delta = Duration - originalDuration;
        field.Broadcast(RoomTimerPacket.Modify(this, delta));
    }

    public void Update(long tickCount) {
        if (!started) {
            if (field.Players.IsEmpty) {
                return; // Don't start timer until first player enters
            }
            StartTick = (int) tickCount;
            field.Broadcast(RoomTimerPacket.Start(this));
            started = true;
        }

        if (tickCount > StartTick + Duration) {
            // Teleport all players to their return map
            TeleportPlayersOut(field);

            // If this is a dungeon lobby, also teleport players from sub-fields
            if (field is DungeonFieldManager dungeonField) {
                foreach (DungeonFieldManager roomField in dungeonField.RoomFields.Values) {
                    TeleportPlayersOut(roomField);
                }
            }

            logger.Debug("Room timer expired, disposing field {FieldId}", field.MapId);
            field.Dispose();
        }
    }

    private static void TeleportPlayersOut(FieldManager targetField) {
        foreach ((int objectId, FieldPlayer player) in targetField.Players) {
            int returnMapId = player.Value.Character.ReturnMaps.IsEmpty
                ? Constant.DefaultReturnMapId
                : player.Value.Character.ReturnMaps.Peek();
            player.Session.Send(player.Session.PrepareField(returnMapId)
                ? FieldEnterPacket.Request(player)
                : FieldEnterPacket.Error(MigrationError.s_move_err_default));
        }
    }

    public bool Expired(long tickCount) => tickCount > StartTick + Duration;
}
