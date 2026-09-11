using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Maple2.Database.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maple2.Server.Tests.Persistence;

public class DatabaseRequestTests {
    [Test]
    public void UncommittedDisposeRollsBackAndDisposesTransactionBeforeContext() {
        var context = new SaveContext();
        var transaction = new Transaction(context);
        using (var request = new Request(context, transaction)) {
            Assert.That(request.SaveChanges(), Is.True);
        }

        Assert.Multiple(() => {
            Assert.That(transaction.Commits, Is.Zero);
            Assert.That(transaction.Rollbacks, Is.EqualTo(1));
            Assert.That(transaction.Disposals, Is.EqualTo(1));
            Assert.That(context.Disposed, Is.True);
        });
    }

    [Test]
    public void ExplicitSuccessfulCommitDisposesTransactionAndIsNotRepeatedOnDispose() {
        var context = new SaveContext();
        var transaction = new Transaction(context);
        using (var request = new Request(context, transaction)) {
            Assert.That(request.SaveChanges(), Is.True);
            Assert.That(request.Commit(), Is.True);
            Assert.That(transaction.Disposals, Is.EqualTo(1));
            Assert.That(request.Commit(), Is.False);
        }
        Assert.That(transaction.Commits, Is.EqualTo(1));
        Assert.That(transaction.Rollbacks, Is.Zero);
        Assert.That(transaction.Disposals, Is.EqualTo(1));
    }

    [Test]
    public void LaterSaveFailurePreventsPartialCommitEvenIfCallerRetries() {
        var context = new SaveContext();
        var transaction = new Transaction(context);
        using (var request = new Request(context, transaction)) {
            Assert.That(request.SaveChanges(), Is.True);
            context.Fail = true;
            Assert.That(request.SaveChanges(), Is.False);
            context.Fail = false;
            Assert.That(request.SaveChanges(), Is.False);
            Assert.That(request.HasFailed, Is.True);
            Assert.That(request.Commit(), Is.False);
        }
        Assert.That(transaction.Commits, Is.Zero);
        Assert.That(transaction.Rollbacks, Is.EqualTo(1));
        Assert.That(transaction.Disposals, Is.EqualTo(1));
    }

    [Test]
    public void TransactionConcurrencyFailureIsNotRetriedAsLastWriteWins() {
        var context = new SaveContext { ConcurrencyOnce = true };
        var transaction = new Transaction(context);
        using (var request = new Request(context, transaction)) {
            Assert.That(request.SaveChanges(), Is.False);
            Assert.That(context.Attempts, Is.EqualTo(1));
            Assert.That(request.HasFailed, Is.True);
            Assert.That(request.Commit(), Is.False);
        }
        Assert.That(transaction.Commits, Is.Zero);
        Assert.That(transaction.Rollbacks, Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CommitFailureRollsBackAndReleasesTransactionResources(bool rollbackFails) {
        var context = new SaveContext();
        var transaction = new Transaction(context) { FailCommit = true, FailRollback = rollbackFails };
        using (var request = new Request(context, transaction)) {
            InvalidOperationException? error = Assert.Throws<InvalidOperationException>(() => request.Commit());
            Assert.That(error!.Message, Is.EqualTo("Injected commit failure."));
            Assert.That(request.HasFailed, Is.True);
        }
        Assert.That(transaction.Rollbacks, Is.EqualTo(1));
        Assert.That(transaction.Disposals, Is.EqualTo(1));
    }

    [Test]
    public void NestedBeginCannotReplaceTheOwnedTransaction() {
        var context = new SaveContext();
        var transaction = new Transaction(context);
        using (var request = new Request(context, transaction)) {
            Assert.Throws<InvalidOperationException>(() => request.BeginTransaction());
            Assert.That(transaction.Disposals, Is.Zero);
            Assert.That(request.Commit(), Is.True);
        }
        Assert.That(transaction.Commits, Is.EqualTo(1));
        Assert.That(transaction.Disposals, Is.EqualTo(1));
    }

    [Test]
    public void ExplicitRollbackCannotBeFollowedByASuccessfulCommit() {
        var context = new SaveContext();
        var transaction = new Transaction(context);
        using (var request = new Request(context, transaction)) {
            request.Rollback();
            Assert.That(request.HasFailed, Is.True);
            Assert.That(request.SaveChanges(), Is.False);
            Assert.That(request.Commit(), Is.False);
        }
        Assert.That(transaction.Commits, Is.Zero);
        Assert.That(transaction.Rollbacks, Is.EqualTo(1));
        Assert.That(transaction.Disposals, Is.EqualTo(1));
    }

    private sealed class Request : DatabaseRequest<SaveContext> {
        public Request(SaveContext context, IDbContextTransaction transaction) : base(context, NullLogger.Instance) {
            typeof(DatabaseRequest<SaveContext>).GetField("transaction", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, transaction);
        }
    }

    private sealed class SaveContext : DbContext {
        public bool Fail;
        public bool Disposed;
        public bool ConcurrencyOnce;
        public int Attempts;

        public override int SaveChanges(bool acceptAllChangesOnSuccess) {
            Attempts++;
            if (ConcurrencyOnce && Attempts == 1) {
                throw new DbUpdateConcurrencyException("Injected stale snapshot.");
            }
            if (Fail) {
                throw new DbUpdateException("Injected later component failure.");
            }
            return 1;
        }

        public override void Dispose() {
            Disposed = true;
            base.Dispose();
        }
    }

    private sealed class Transaction(SaveContext context) : IDbContextTransaction {
        public Guid TransactionId { get; } = Guid.NewGuid();
        public int Commits;
        public int Rollbacks;
        public int Disposals;
        public bool FailCommit;
        public bool FailRollback;

        public void Commit() {
            Commits++;
            if (FailCommit) {
                throw new InvalidOperationException("Injected commit failure.");
            }
        }

        public void Rollback() {
            Assert.That(context.Disposed, Is.False);
            Rollbacks++;
            if (FailRollback) {
                throw new InvalidOperationException("Injected rollback failure.");
            }
        }

        public void Dispose() {
            Assert.That(context.Disposed, Is.False);
            Disposals++;
        }

        public Task CommitAsync(CancellationToken cancellationToken = default) {
            Commit();
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default) {
            Rollback();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
