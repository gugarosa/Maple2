using System;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Maple2.Server.Web.ViewResults;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;

namespace Maple2.Server.Web.Helpers;

public static class AccountPageProtection {
    public const string RATE_LIMIT_POLICY = "account-registration";

    public static void ConfigureRateLimiting(RateLimiterOptions options) {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = async (context, cancellationToken) => {
            if (!IsAccountRequest(context.HttpContext)) {
                context.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
                await context.HttpContext.Response.WriteAsync("Too many requests. Please try again later.", cancellationToken);
                return;
            }
            TimeSpan? retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan delay) ? delay : null;
            await AccountNoticeResult.Throttled(retryAfter).ExecuteAsync(context.HttpContext);
        };
        options.AddPolicy(RATE_LIMIT_POLICY, context => RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
    }

    public static async Task RequireHttps(HttpContext context, RequestDelegate next) {
        if (IsAccountRequest(context) && !context.Request.IsHttps) {
            await AccountNoticeResult.HttpsRequired().ExecuteAsync(context);
            return;
        }
        await next(context);
    }

    private static bool IsAccountRequest(HttpContext context) =>
        context.Request.Path.StartsWithSegments("/account", StringComparison.OrdinalIgnoreCase);
}
