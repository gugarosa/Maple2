using Maple2.Model.Game;
using Maple2.PacketLib.Tools;
using Maple2.Server.Core.Constants;
using Maple2.Server.Game.PacketHandlers.Field;
using Maple2.Server.Game.Packets;
using Maple2.Server.Game.Session;

namespace Maple2.Server.Game.PacketHandlers;

public class WorldMapHandler : FieldPacketHandler {
    public override RecvOp OpCode => RecvOp.RequestWorldmap;

    private enum Command : byte {
        Load = 0,
        Population = 1,
    }

    public override void Handle(GameSession session, IByteReader packet) {
        var command = packet.Read<Command>();

        switch (command) {
            case Command.Load:
                HandleLoad(session, packet);
                return;
            case Command.Population:
                HandlePopulation(session, packet);
                return;
        }
    }

    private static void HandleLoad(GameSession session, IByteReader packet) {
        packet.ReadByte();

        // 102 = Victoria, 103 = Karkar, 105 = Kritias
        int mapCode = packet.ReadInt();
        short channel = GameServer.GetChannel();
        ICollection<MapPopulation> populations = session.FieldFactory.GetPopulations(channel);
        session.Send(WorldMapPacket.Load(new List<ICollection<MapWorldBoss>>(), populations));
    }

    private static void HandlePopulation(GameSession session, IByteReader packet) {
        // 102 = Victoria, 103 = Karkar, 105 = Kritias
        int mapCode = packet.ReadInt();
        short channel = GameServer.GetChannel();
        ICollection<MapPopulation> populations = session.FieldFactory.GetPopulations(channel);
        session.Send(WorldMapPacket.Population(populations));
    }
}
