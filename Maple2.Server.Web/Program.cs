using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using Maple2.Server.Core.Modules;
using Maple2.Server.Web.Helpers;
using Maple2.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

// Force Globalization to en-US because we use periods instead of commas for decimals
CultureInfo.CurrentCulture = new("en-US");

if (!string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase)) {
    DotEnv.Load();
}

IConfigurationRoot configRoot = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", true, true)
    .AddEnvironmentVariables()
    .Build();
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(configRoot)
    .CreateLogger();

string listenPort = Environment.GetEnvironmentVariable("WEB_BIND_PORT")
    ?? Environment.GetEnvironmentVariable("WEB_PORT") ?? "4000";
if (!ushort.TryParse(listenPort, out ushort webPort) || webPort == 0) {
    throw new InvalidOperationException("WEB_BIND_PORT (or WEB_PORT) must be a port from 1 to 65535.");
}
foreach (string name in new[] { "SERVER_SOURCE_URL", "PLAYER_WEBSITE_URL" }) {
    string? url = Environment.GetEnvironmentVariable(name);
    if (url != null && (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
        uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0)) {
        throw new InvalidOperationException($"{name} must be an absolute HTTPS URL without embedded credentials.");
    }
}

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
bool requireHttpsRegistration = builder.Configuration.GetValue<bool>("REQUIRE_HTTPS_REGISTRATION");
builder.Services.Configure<ForwardedHeadersOptions>(options => {
    // The Azure proxy shares Web's network namespace and connects over loopback.
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});
builder.Services.AddAntiforgery(options => options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest);
builder.WebHost.UseKestrel(options => {
    options.Listen(new IPEndPoint(IPAddress.Any, webPort), listen => {
        listen.Protocols = HttpProtocols.Http1;
    });
    // Omitting for now since HTTPS requires a certificate
    // options.Listen(new IPEndPoint(IPAddress.Any, 443), listen => {
    //     listen.UseHttps();
    //     listen.Protocols = HttpProtocols.Http1;
    // });
});
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(15));
builder.Services.AddMemoryCache();
builder.Services.AddControllersWithViews();
builder.Services.AddRateLimiter(AccountPageProtection.ConfigureRateLimiting);

builder.Logging.ClearProviders();
builder.Logging.AddSerilog(dispose: true);

builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory());
builder.Host.ConfigureContainer<ContainerBuilder>(autofac => {
    // Database modules
    autofac.RegisterModule<WebDbModule>();
    autofac.RegisterModule<DataDbModule>();
    autofac.RegisterModule<GameDbModule>();
});

WebApplication app = builder.Build();
app.UseForwardedHeaders();
if (requireHttpsRegistration) {
    app.Use(AccountPageProtection.RequireHttps);
}
app.UseRouting();
app.UseRateLimiter();
app.MapControllers();

var provider = app.Services.GetRequiredService<IActionDescriptorCollectionProvider>();
IEnumerable<ActionDescriptor> routes = provider.ActionDescriptors.Items
    .Where(x => x.AttributeRouteInfo != null);

Log.Logger.Debug("========== ROUTES ==========");
foreach (ActionDescriptor route in routes) {
    Log.Logger.Debug("{Route}", route.AttributeRouteInfo?.Template);
}

await app.RunAsync();
