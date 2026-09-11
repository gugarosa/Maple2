using Grpc.Core;
using System.Data.Common;
using System.Threading.RateLimiting;
using Maple2.Database.Model;
using Maple2.Database.Storage;
using Maple2.Model.Game;
using Maple2.Model.Metadata;
using Maple2.Model.Validators;
using Microsoft.EntityFrameworkCore;
using Serilog;
using ILogger = Serilog.ILogger;

// TODO: Move this to a Global server
// ReSharper disable once CheckNamespace
namespace Maple2.Server.Global.Service;

public partial class GlobalService : Global.GlobalBase, IDisposable {
    private readonly GameStorage gameStorage;
    private readonly PartitionedRateLimiter<string> loginLimiter = PartitionedRateLimiter.Create<string, string>(ip =>
        RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions {
            PermitLimit = 20,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));

    private readonly ILogger logger = Log.Logger.ForContext<GlobalService>();

    public GlobalService(GameStorage gameStorage) {
        this.gameStorage = gameStorage;
    }

    public override Task<LoginResponse> Login(LoginRequest request, ServerCallContext context) {
        using RateLimitLease lease = loginLimiter.AttemptAcquire(request.ClientIp);
        if (!lease.IsAcquired) {
            logger.Warning("Rate-limited a login attempt from {ClientIp}", request.ClientIp);
            return Task.FromResult(new LoginResponse {
                Code = LoginResponse.Types.Code.ErrorId,
                Message = "Too many login attempts. Please wait a minute and try again.",
            });
        }
        string username = AccountCredentialValidator.NormalizeUsername(request.Username);
        if (!AccountCredentialValidator.ValidLoginUsername(username) ||
            !AccountCredentialValidator.ValidLoginPassword(request.Password)) {
            return Task.FromResult(new LoginResponse {
                Code = LoginResponse.Types.Code.ErrorPassword,
                Message = "Enter your registered username and password.",
            });
        }

        if (!Guid.TryParse(request.MachineId, out Guid machineId) || machineId == Guid.Empty) {
            logger.Warning("Rejected a login with an invalid machine identifier");
            return Task.FromResult(new LoginResponse {
                Code = LoginResponse.Types.Code.SessionError,
                Message = "Invalid client session. Restart the client and try again.",
            });
        }
        string clientIp = request.ClientIp;

        try {
            using GameStorage.Request db = gameStorage.Context();

            // Hardware and IP restrictions apply before account authentication.
            (bool IsBanned, Ban? Ban) hwStatus = db.GetBanStatus(null, clientIp, machineId);
            if (hwStatus is { IsBanned: true, Ban: not null }) {
                return Task.FromResult(new LoginResponse {
                    Code = LoginResponse.Types.Code.Restricted,
                    Message = hwStatus.Ban.Reason,
                    AccountId = 0,
                    BanStart = new DateTimeOffset(hwStatus.Ban.CreatedAt).ToUnixTimeSeconds(),
                    BanExpiry = new DateTimeOffset(hwStatus.Ban.ExpiresAt).ToUnixTimeSeconds(),
                });
            }

            Account? account = db.GetAccount(username);
            if (account is null || !db.VerifyPassword(account.Id, request.Password)) {
                return Task.FromResult(new LoginResponse {
                    Code = LoginResponse.Types.Code.ErrorPassword,
                    Message = "Invalid username or password. Register an account before signing in.",
                });
            }

            if (account.MachineId == Guid.Empty) {
                if (!db.UpdateMachineId(account.Id, machineId)) {
                    logger.Error("Could not bind the first client session for account {AccountId}", account.Id);
                    return Task.FromResult(new LoginResponse {
                        Code = LoginResponse.Types.Code.ErrorDb,
                        Message = "Could not save the login session. Please try again.",
                    });
                }
            } else if (account.MachineId != machineId) {
                logger.Warning("MachineId mismatch for account {AccountId}", account.Id);
                if (Constant.BlockLoginWithMismatchedMachineId) {
                    return Task.FromResult(new LoginResponse {
                        Code = LoginResponse.Types.Code.BlockNexonSn,
                        Message = "MachineId mismatch",
                    });
                }
            }

            (bool IsBanned, Ban? Ban) status = db.GetBanStatus(account.Id, clientIp, machineId);
            if (status is { IsBanned: true, Ban: not null }) {
                return Task.FromResult(new LoginResponse {
                    Code = LoginResponse.Types.Code.Restricted,
                    Message = status.Ban.Reason,
                    AccountId = account.Id,
                    BanStart = new DateTimeOffset(status.Ban.CreatedAt).ToUnixTimeSeconds(),
                    BanExpiry = new DateTimeOffset(status.Ban.ExpiresAt).ToUnixTimeSeconds(),
                });
            }

            return Task.FromResult(new LoginResponse { AccountId = account.Id });
        } catch (Exception ex) when (ex is DbUpdateException || ex.GetBaseException() is DbException) {
            logger.Error(ex, "Could not complete account authentication");
            return Task.FromResult(new LoginResponse {
                Code = LoginResponse.Types.Code.ErrorDb,
                Message = "The account service is temporarily unavailable. Please try again.",
            });
        }
    }

    public void Dispose() => loginLimiter.Dispose();
}
