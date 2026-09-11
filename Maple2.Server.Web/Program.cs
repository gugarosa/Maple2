using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.RateLimiting;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using Maple2.Server.Core.Modules;
using Maple2.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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

int.TryParse(Environment.GetEnvironmentVariable("WEB_PORT") ?? "4000", out int webPort);

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
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
builder.Services.AddRateLimiter(options => {
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) => {
        context.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
        await context.HttpContext.Response.WriteAsync(
            "Too many registration attempts. Wait a minute, then reload the registration page.", cancellationToken);
    };
    options.AddPolicy("account-registration", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
});

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
