using System;
using System.Data.Common;
using Maple2.Tools;

namespace Maple2.Server.Tests.Tools;

public class DatabaseConnectionStringTests {
    [Test]
    public void CredentialsCannotChangeTheSelectedDatabase() {
        const string password = "test;Database=another;Password=value";
        string connection = DatabaseConnectionString.Build("localhost", "3306", "game-server", "test-user", password);
        var parsed = new DbConnectionStringBuilder { ConnectionString = connection };
        Assert.That(parsed["Database"], Is.EqualTo("game-server"));
        Assert.That(parsed["Password"], Is.EqualTo(password));
    }

    [TestCase("0")]
    [TestCase("65536")]
    [TestCase("3306;Database=other")]
    [TestCase("")]
    public void InvalidPortsFailBeforeConnecting(string port) {
        Assert.Throws<ArgumentException>(() => DatabaseConnectionString.Build("localhost", port, "game-server", "test", "test"));
    }

    [TestCase("mysql")]
    [TestCase("SYS")]
    [TestCase("information_schema")]
    [TestCase("performance_schema")]
    [TestCase(" game-server")]
    [TestCase("")]
    public void SystemOrInvalidDatabaseNamesAreRejected(string database) {
        Assert.Throws<ArgumentException>(() => DatabaseConnectionString.Build("localhost", "3306", database, "test", "test"));
    }
}
