using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Grpc.Core;
using Maple2.Database.Storage;
using Maple2.Server.Game.Session;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;

namespace Maple2.Server.Tests.Persistence;

[Explicit("Requires MAPLE2_RUN_DB_TESTS=1 and isolated validation database configuration.")]
[NonParallelizable]
public class DatabaseRequestPersistenceTests {
    private DbContextOptions<RowsContext> options = null!;
    private bool databaseCreated;

    [OneTimeSetUp]
    public void CreateIsolatedDatabase() {
        if (Environment.GetEnvironmentVariable("MAPLE2_RUN_DB_TESTS") != "1") {
            Assert.Ignore("Set MAPLE2_RUN_DB_TESTS=1 to run isolated persistence tests.");
        }
        string metadataDatabase = Required("DATA_DB_NAME");
        if (!metadataDatabase.StartsWith("maple2_validation_", StringComparison.Ordinal)) {
            throw new InvalidOperationException("A maple2_validation_ metadata database is required.");
        }
        var connection = new MySqlConnectionStringBuilder {
            Server = Required("DB_IP"),
            Port = uint.Parse(Required("DB_PORT"), CultureInfo.InvariantCulture),
            UserID = Required("DB_USER"),
            Password = Required("DB_PASSWORD"),
            Database = metadataDatabase,
        };
        ServerVersion version = ServerVersion.AutoDetect(connection.ConnectionString);
        connection.Database = "maple2_validation_request_" + Guid.NewGuid().ToString("N")[..12];
        options = new DbContextOptionsBuilder<RowsContext>().UseMySql(connection.ConnectionString, version).Options;
        using var context = new RowsContext(options);
        if (context.Database.CanConnect()) {
            throw new InvalidOperationException("Refusing to reuse an existing validation database.");
        }
        databaseCreated = true;
        context.Database.EnsureCreated();
    }

    [OneTimeTearDown]
    public void RemoveIsolatedDatabase() {
        if (databaseCreated) {
            using var context = new RowsContext(options);
            context.Database.EnsureDeleted();
        }
    }

    [SetUp]
    public void ClearRows() {
        using var context = new RowsContext(options);
        context.Rows.ExecuteDelete();
    }

    [Test]
    public void ExplicitSuccessfulCommitPersistsAllComponents() {
        using (var request = new RowsRequest(new RowsContext(options))) {
            request.BeginTransaction();
            Assert.That(request.Add(1, 10), Is.True);
            Assert.That(request.Add(2, 20), Is.True);
            Assert.That(request.Commit(), Is.True);
        }
        using var context = new RowsContext(options);
        Assert.That(context.Rows.Count(), Is.EqualTo(2));
    }

    [Test]
    public void UncommittedDisposeUndoesPreviouslyFlushedWrites() {
        using (var request = new RowsRequest(new RowsContext(options))) {
            request.BeginTransaction();
            Assert.That(request.Add(1, 10), Is.True);
        }
        AssertNoRows();
    }

    [Test]
    public void LaterDatabaseFailureCannotCommitAnEarlierComponent() {
        using (var request = new RowsRequest(new RowsContext(options))) {
            request.BeginTransaction();
            Assert.That(request.Add(1, 10), Is.True);
            Assert.That(request.Add(2, 10), Is.False); // Unique-value violation after the first flush.
            Assert.That(request.Commit(), Is.False);
        }
        AssertNoRows();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FailedComponentOrExpiredLeaseRollsBackTheSessionSaveBoundary(bool expired) {
        TargetInvocationException? failure = Assert.Throws<TargetInvocationException>(() => {
            using var request = new RowsRequest(new RowsContext(options));
            request.BeginTransaction();
            Assert.That(request.Add(1, 10), Is.True);
            long expiresAt = DateTimeOffset.UtcNow.AddSeconds(expired ? -1 : 30).ToUnixTimeMilliseconds();
            typeof(GameSession).GetMethod("RequireSaveSuccess", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [expired, "Quest", expiresAt]);
            Assert.Fail("A failed component or expired lease must not reach commit.");
        });
        Assert.That(failure!.InnerException, expired ? Is.TypeOf<RpcException>() : Is.TypeOf<InvalidOperationException>());
        AssertNoRows();
    }

    [Test]
    public void RequestCannotNestOrCommitAnExternallyOwnedTransaction() {
        using (var context = new RowsContext(options))
        using (var transaction = context.Database.BeginTransaction())
        using (var request = new RowsRequest(context)) {
            Assert.That(request.IsTransaction, Is.True);
            Assert.That(request.Add(1, 10), Is.True);
            Assert.Throws<InvalidOperationException>(() => request.BeginTransaction());
            Assert.That(request.Commit(), Is.False);
            transaction.Commit();
        }
        using var verify = new RowsContext(options);
        Assert.That(verify.Rows.Count(), Is.EqualTo(1));
    }

    private void AssertNoRows() {
        using var context = new RowsContext(options);
        Assert.That(context.Rows.Count(), Is.Zero);
    }

    private static string Required(string name) {
        return Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"{name} is required.");
    }

    private sealed class RowsContext(DbContextOptions<RowsContext> configuration) : DbContext(configuration) {
        public DbSet<SaveRow> Rows => Set<SaveRow>();

        protected override void OnModelCreating(ModelBuilder builder) {
            builder.Entity<SaveRow>().HasKey(row => row.Id);
            builder.Entity<SaveRow>().HasIndex(row => row.Value).IsUnique();
        }
    }

    private sealed class RowsRequest(RowsContext context) : DatabaseRequest<RowsContext>(context, NullLogger.Instance) {
        public bool Add(int id, int value) {
            Context.Rows.Add(new SaveRow { Id = id, Value = value });
            return SaveChanges();
        }
    }

    private sealed class SaveRow {
        public int Id { get; set; }
        public int Value { get; set; }
    }
}
