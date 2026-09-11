using Grpc.Core;

namespace Maple2.Server.Game.Service;

public partial class ChannelService {
    public override Task<GameResetResponse> GameReset(GameResetRequest request, ServerCallContext context) {
        switch (request.ResetCase) {
            case GameResetRequest.ResetOneofCase.Daily:
                return Task.FromResult(Daily());
            case GameResetRequest.ResetOneofCase.Weekly:
                return Task.FromResult(Weekly());
            case GameResetRequest.ResetOneofCase.Monthly:
                return Task.FromResult(Monthly());
            default:
                throw new RpcException(new Status(StatusCode.InvalidArgument, "A reset period is required."));
        }
    }

    private GameResetResponse Daily() {
        return new GameResetResponse { Error = server.DailyReset() ? 0 : 1 };
    }

    private GameResetResponse Weekly() {
        return new GameResetResponse { Error = server.WeeklyReset() ? 0 : 1 };
    }

    private GameResetResponse Monthly() {
        return new GameResetResponse { Error = server.MonthlyReset() ? 0 : 1 };
    }
}
