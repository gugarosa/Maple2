using System;
using System.Data.Common;
using System.Globalization;

namespace Maple2.Tools;

public static class DatabaseConnectionString {
    public static string Build(string? server, string? port, string? database, string? user, string? password) {
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password)) {
            throw new ArgumentException("DB_IP, DB_USER and DB_PASSWORD must be configured.");
        }
        ValidateDatabaseName(database);
        if (!ushort.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out ushort number) || number == 0) {
            throw new ArgumentException("DB_PORT must be an integer from 1 to 65535.");
        }
        return new DbConnectionStringBuilder {
            ["Server"] = server,
            ["Port"] = number,
            ["Database"] = database,
            ["User"] = user,
            ["Password"] = password,
            ["oldguids"] = true,
        }.ConnectionString;
    }

    public static void ValidateDatabaseName(string? name) {
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim()) {
            throw new ArgumentException("Database names must be nonempty and have no surrounding whitespace.");
        }
        if (name.ToLowerInvariant() is "mysql" or "sys" or "information_schema" or "performance_schema") {
            throw new ArgumentException("System databases cannot be used for Maple2 metadata or player data.");
        }
    }
}
