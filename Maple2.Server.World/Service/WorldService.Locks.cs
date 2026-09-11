using System.Collections.Concurrent;
using Grpc.Core;

namespace Maple2.Server.World.Service;

public partial class WorldService {
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);
    private sealed record AccountLock(Guid Owner, DateTimeOffset ExpiresAt);
    private static readonly ConcurrentDictionary<long, AccountLock> Locks = new();

    public override Task<LockResponse> AcquireLock(LockRequest request, ServerCallContext context) {
        Guid owner = ValidateLockRequest(request);
        while (true) {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var lease = new AccountLock(owner, now.Add(LockTimeout));
            if (Locks.TryGetValue(request.AccountId, out AccountLock? current)) {
                if (current.ExpiresAt > now) {
                    return Task.FromResult(current.Owner == owner
                        ? new LockResponse { ExpiresAt = current.ExpiresAt.ToUnixTimeMilliseconds() }
                        : new LockResponse { Error = "Lock already held." });
                }
                if (!Locks.TryUpdate(request.AccountId, lease, current)) {
                    continue;
                }
            } else if (!Locks.TryAdd(request.AccountId, lease)) {
                continue;
            }

            return Task.FromResult(new LockResponse { ExpiresAt = lease.ExpiresAt.ToUnixTimeMilliseconds() });
        }
    }

    public override Task<LockResponse> ReleaseLock(LockRequest request, ServerCallContext context) {
        Guid owner = ValidateLockRequest(request);
        if (Locks.TryGetValue(request.AccountId, out AccountLock? current) &&
            current.Owner == owner && current.ExpiresAt > DateTimeOffset.UtcNow &&
            ((ICollection<KeyValuePair<long, AccountLock>>) Locks).Remove(new KeyValuePair<long, AccountLock>(request.AccountId, current))) {
            return Task.FromResult(new LockResponse());
        }
        logger.Warning("Account lock release rejected for account {AccountId}: lease is not held by this caller", request.AccountId);
        return Task.FromResult(new LockResponse {
            Error = "Lock expired or not held by this owner.",
        });
    }

    private Guid ValidateLockRequest(LockRequest request) {
        if (request.AccountId <= 0 || !Guid.TryParse(request.OwnerToken, out Guid owner) || owner == Guid.Empty) {
            logger.Warning("Invalid account lock request for account {AccountId}", request.AccountId);
            throw new RpcException(new Status(StatusCode.InvalidArgument, "An account ID and a nonempty owner token are required."));
        }
        return owner;
    }
}
