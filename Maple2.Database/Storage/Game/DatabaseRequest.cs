using Maple2.Database.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Maple2.Database.Storage;

public abstract class DatabaseRequest<TContext>(TContext context, ILogger logger) : IDisposable
    where TContext : DbContext {
    protected readonly TContext Context = context;
    protected readonly ILogger Logger = logger;

    private IDbContextTransaction? transaction;
    public bool IsTransaction => Context.Database.CurrentTransaction != null;
    public bool HasFailed { get; private set; }

    public void BeginTransaction() {
        if (transaction != null || IsTransaction) {
            throw new InvalidOperationException("A transaction is already active on this request.");
        }
        if (HasFailed) {
            throw new InvalidOperationException("A failed request cannot begin another transaction.");
        }
        transaction = Context.Database.BeginTransaction();
    }

    public bool Commit() {
        if (transaction == null) {
            Logger.LogError("Cannot commit a request without an owned transaction");
            return false;
        }

        if (HasFailed) {
            Logger.LogError("Rolling back a request after a failed save");
            Rollback();
            return false;
        }

        try {
            transaction.Commit();
            return true;
        } catch (Exception ex) {
            HasFailed = true;
            Logger.LogError(ex, "Failed to commit database request");
            try {
                Rollback();
            } catch (Exception rollbackError) {
                Logger.LogError(rollbackError, "Failed to roll back after an unconfirmed commit");
            }
            throw;
        } finally {
            transaction?.Dispose();
            transaction = null;
        }
    }

    public void Rollback() {
        if (transaction == null) {
            return;
        }
        IDbContextTransaction current = transaction;
        transaction = null;
        HasFailed = true;
        try {
            current.Rollback();
        } finally {
            current.Dispose();
        }
    }

    public bool SaveChanges() {
        if (HasFailed) {
            return false;
        }
        try {
            if (transaction != null || IsTransaction) {
                Context.SaveChanges();
                return true;
            }
            HasFailed = !Context.TrySaveChanges();
            return !HasFailed;
        } catch (Exception ex) {
            HasFailed = true;
            Logger.LogError(ex, "Database request save failed; transaction must be rolled back");
            return false;
        }
    }

    public void Dispose() {
        try {
            Rollback();
        } finally {
            Context.Dispose();
        }
    }
}
