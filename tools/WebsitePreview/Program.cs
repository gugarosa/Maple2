using System.Net;
using Maple2.Server.Web.Helpers;
using Maple2.Server.Web.Model;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using WebsitePreview;

if (args.Length != 4 || args[0] != "--root" || args[2] != "--output") {
    throw new ArgumentException("Usage: WebsitePreview --root <worktree> --output <directory inside worktree>");
}
string root = Path.GetFullPath(args[1]);
string output = Path.GetFullPath(args[3], root);
if (!output.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) {
    throw new ArgumentException("Fixture output must be inside the supplied worktree.");
}
if (!File.Exists(Path.Combine(root, "Maple2.Server.Web", "Maple2.Server.Web.csproj"))) {
    throw new ArgumentException("The supplied root is not the MS2 worktree.");
}

string? originalWebsite = Environment.GetEnvironmentVariable("PLAYER_WEBSITE_URL");
string? originalSource = Environment.GetEnvironmentVariable("SERVER_SOURCE_URL");
Environment.SetEnvironmentVariable("PLAYER_WEBSITE_URL", AccountChecks.PlayerWebsite);
Environment.SetEnvironmentVariable("SERVER_SOURCE_URL", "https://github.com/gugarosa/Maple2");
try {
    WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions {
        ApplicationName = typeof(AccountFixtureController).Assembly.FullName,
        ContentRootPath = Path.Combine(root, "tools", "WebsitePreview"),
        EnvironmentName = Environments.Production,
        Args = [],
    });
    builder.Configuration.Sources.Clear();
    builder.Logging.ClearProviders();
    builder.Logging.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning);
    builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
    builder.Services.AddControllersWithViews().ConfigureApplicationPartManager(parts => {
        parts.ApplicationParts.Clear();
        parts.ApplicationParts.Add(new AssemblyPart(typeof(AccountFixtureController).Assembly));
        parts.ApplicationParts.Add(new CompiledRazorAssemblyPart(typeof(RegistrationForm).Assembly));
    });
    builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
    builder.Services.AddAntiforgery(options => options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest);
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);
    builder.Services.AddRateLimiter(AccountPageProtection.ConfigureRateLimiting);
    builder.Services.AddSingleton<FixtureCalls>();

    await using WebApplication app = builder.Build();
    ControllerFeature controllers = new();
    app.Services.GetRequiredService<ApplicationPartManager>().PopulateFeature(controllers);
    Type[] allowedControllers = [typeof(AccountFixtureController), typeof(OutsideFixtureController)];
    if (controllers.Controllers.Count != allowedControllers.Length ||
        controllers.Controllers.Any(controller => !allowedControllers.Contains(controller.AsType())) ||
        builder.Services.Any(service => service.ServiceType.FullName == "Maple2.Database.Storage.GameStorage")) {
        throw new InvalidOperationException("The preview must expose only its inert fixture controllers, without game storage.");
    }

    app.UseForwardedHeaders();
    app.Use(AccountPageProtection.RequireHttps);
    app.UseRouting();
    app.UseRateLimiter();
    app.MapControllers();
    try {
        await app.StartAsync();
        Uri origin = new(app.Urls.Single());
        using FixtureProxyHandler handler = new(origin);
        using HttpClient client = new(handler) {
            BaseAddress = origin,
            Timeout = TimeSpan.FromSeconds(15),
        };
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        AccountChecks checks = new(client, origin, root, output, app.Services.GetRequiredService<FixtureCalls>());
        await checks.RunAsync();
    } finally {
        await app.StopAsync();
    }
} finally {
    Environment.SetEnvironmentVariable("PLAYER_WEBSITE_URL", originalWebsite);
    Environment.SetEnvironmentVariable("SERVER_SOURCE_URL", originalSource);
}
