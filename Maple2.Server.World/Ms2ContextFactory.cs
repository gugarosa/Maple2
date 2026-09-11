using Maple2.Database.Context;
using Maple2.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Maple2.Server.World;

public sealed class Ms2ContextFactory : IDesignTimeDbContextFactory<Ms2Context> {
    public Ms2Context CreateDbContext(string[] args) {
        string? server = Environment.GetEnvironmentVariable("DB_IP");
        string? port = Environment.GetEnvironmentVariable("DB_PORT");
        string? database = Environment.GetEnvironmentVariable("GAME_DB_NAME");
        string? user = Environment.GetEnvironmentVariable("DB_USER");
        string? password = Environment.GetEnvironmentVariable("DB_PASSWORD");

        string gameDbConnection = DatabaseConnectionString.Build(server, port, database, user, password);

        DbContextOptions options = new DbContextOptionsBuilder()
            .UseMySql(gameDbConnection, ServerVersion.AutoDetect(gameDbConnection), options => {
                options.MigrationsAssembly(typeof(Ms2ContextFactory).Assembly.FullName);
            }).Options;
        return new Ms2Context(options);
    }
}
