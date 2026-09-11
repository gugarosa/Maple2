using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Maple2.Server.World.Service;
using Serilog;

namespace Maple2.Server.Tests.World.Service;

public class AccountLockTests {
    private static long nextAccount = 7000000000000;
    private long accountId;
    private WorldService service = null!;
    private IDictionary locks = null!;

    [SetUp]
    public void SetUp() {
        accountId = Interlocked.Increment(ref nextAccount);
        service = (WorldService) RuntimeHelpers.GetUninitializedObject(typeof(WorldService));
        typeof(WorldService).GetField("logger", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, Log.Logger);
        locks = (IDictionary) typeof(WorldService).GetField("Locks", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    }

    [TearDown]
    public void TearDown() {
        locks.Remove(accountId);
    }

    [Test]
    public async Task OnlyAcquiringOwnerCanReleaseAndRetriesDoNotExtendTheLease() {
        LockRequest owner = Request();
        LockRequest other = Request();
        LockResponse acquired = await service.AcquireLock(owner, null!);
        Assert.That(acquired.Error, Is.Empty);
        Assert.That(acquired.ExpiresAt, Is.GreaterThan(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        Assert.That(acquired.ExpiresAt, Is.LessThanOrEqualTo(DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeMilliseconds()));

        LockResponse retry = await service.AcquireLock(owner, null!);
        Assert.That(retry.Error, Is.Empty);
        Assert.That(retry.ExpiresAt, Is.EqualTo(acquired.ExpiresAt));
        Assert.That((await service.AcquireLock(other, null!)).Error, Is.Not.Empty);
        Assert.That((await service.ReleaseLock(other, null!)).Error, Is.Not.Empty);
        Assert.That((await service.AcquireLock(other, null!)).Error, Is.Not.Empty);

        Assert.That((await service.ReleaseLock(owner, null!)).Error, Is.Empty);
        Assert.That((await service.AcquireLock(other, null!)).Error, Is.Empty);
        Assert.That((await service.ReleaseLock(owner, null!)).Error, Is.Not.Empty);
        Assert.That((await service.ReleaseLock(other, null!)).Error, Is.Empty);
    }

    [Test]
    public async Task ExpiredHolderCannotReleaseItsReplacement() {
        LockRequest old = Request();
        await service.AcquireLock(old, null!);
        Expire(old);
        Assert.That((await service.ReleaseLock(old, null!)).Error, Is.Not.Empty);

        LockRequest replacement = Request();
        Assert.That((await service.AcquireLock(replacement, null!)).Error, Is.Empty);
        Assert.That((await service.ReleaseLock(old, null!)).Error, Is.Not.Empty);
        Assert.That((await service.AcquireLock(Request(), null!)).Error, Is.Not.Empty);
        Assert.That((await service.ReleaseLock(replacement, null!)).Error, Is.Empty);
    }

    [Test]
    public async Task ConcurrentExpiredLeaseReplacementHasExactlyOneOwner() {
        LockRequest old = Request();
        await service.AcquireLock(old, null!);
        Expire(old);

        LockRequest[] requests = Enumerable.Range(0, 16).Select(_ => Request()).ToArray();
        LockResponse[] responses = await Task.WhenAll(requests.Select(request =>
            Task.Run(() => service.AcquireLock(request, null!)))).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(responses.Count(response => response.Error.Length == 0), Is.EqualTo(1));
        int winner = Array.FindIndex(responses, response => response.Error.Length == 0);

        Assert.That((await service.ReleaseLock(old, null!)).Error, Is.Not.Empty);
        foreach (LockRequest loser in requests.Where((_, index) => index != winner)) {
            Assert.That((await service.ReleaseLock(loser, null!)).Error, Is.Not.Empty);
        }
        Assert.That((await service.AcquireLock(Request(), null!)).Error, Is.Not.Empty);
        Assert.That((await service.ReleaseLock(requests[winner], null!)).Error, Is.Empty);
    }

    [TestCase(0, "e0796e7a66c343879a0b828770e452b0")]
    [TestCase(-1, "e0796e7a66c343879a0b828770e452b0")]
    [TestCase(1, "")]
    [TestCase(1, "not-a-token")]
    [TestCase(1, "00000000000000000000000000000000")]
    public void InvalidRequestsCannotAcquireOrRelease(long account, string token) {
        var request = new LockRequest { AccountId = account, OwnerToken = token };
        RpcException? acquire = Assert.ThrowsAsync<RpcException>(async () => await service.AcquireLock(request, null!));
        RpcException? release = Assert.ThrowsAsync<RpcException>(async () => await service.ReleaseLock(request, null!));
        Assert.That(acquire!.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
        Assert.That(release!.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
    }

    private LockRequest Request() {
        return new LockRequest { AccountId = accountId, OwnerToken = Guid.NewGuid().ToString("N") };
    }

    private void Expire(LockRequest request) {
        object current = locks[accountId]!;
        locks[accountId] = Activator.CreateInstance(current.GetType(), Guid.Parse(request.OwnerToken), DateTimeOffset.UtcNow.AddMinutes(-1))!;
    }
}
