using Grpc.Core;

namespace Maple2.Server.World.Service;

public partial class WorldService {
    public override Task<GameResetResponse> GameReset(GameResetRequest request, ServerCallContext context) {
        return Task.FromResult(new GameResetResponse { Error = worldServer.GameReset(request) ? 0 : 1 });
    }
}
