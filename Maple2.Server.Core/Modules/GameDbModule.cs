using System.Reflection;
using Autofac;
using Maple2.Database.Storage;
using Maple2.Tools;
using Microsoft.EntityFrameworkCore;
using Module = Autofac.Module;

namespace Maple2.Server.Core.Modules;

public class GameDbModule : Module {
    private const string NAME = "GameDbOptions";

    private readonly DbContextOptions options;

    public GameDbModule() {
        string? server = Environment.GetEnvironmentVariable("DB_IP");
        string? port = Environment.GetEnvironmentVariable("DB_PORT");
        string? database = Environment.GetEnvironmentVariable("GAME_DB_NAME");
        string? user = Environment.GetEnvironmentVariable("DB_USER");
        string? password = Environment.GetEnvironmentVariable("DB_PASSWORD");

        string gameDbConnection = DatabaseConnectionString.Build(server, port, database, user, password);

        options = new DbContextOptionsBuilder()
            .UseMySql(gameDbConnection, ServerVersion.AutoDetect(gameDbConnection))
            .Options;
    }

    protected override void Load(ContainerBuilder builder) {
        builder.RegisterInstance(options)
            .Named<DbContextOptions>(NAME);

        builder.RegisterType<GameStorage>()
            .WithParameter(Condition, Resolve)
            .SingleInstance();
    }

    private static bool Condition(ParameterInfo info, IComponentContext context) {
        return info.Name == "options";
    }

    private static DbContextOptions Resolve(ParameterInfo info, IComponentContext context) {
        return context.ResolveNamed<DbContextOptions>(NAME);
    }
}
