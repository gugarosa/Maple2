using System;
using System.Globalization;
using System.Threading.Tasks;
using Maple2.Server.Web.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;

namespace Maple2.Server.Web.ViewResults;

public sealed class AccountNoticeResult : ViewResult {
    private AccountNoticeResult(AccountNotice notice, int statusCode) {
        ViewName = "~/Views/Account/Notice.cshtml";
        StatusCode = statusCode;
        ViewData = new ViewDataDictionary<AccountNotice>(new EmptyModelMetadataProvider(), new ModelStateDictionary()) {
            Model = notice,
        };
    }

    public static AccountNoticeResult Expired() => new(new AccountNotice(
        "This form could not be verified",
        "The form may have expired, or your browser may be blocking cookies. Reload registration and enter your details again."),
        StatusCodes.Status400BadRequest);

    public static AccountNoticeResult Throttled(TimeSpan? retryAfter = null) {
        int? seconds = retryAfter.HasValue
            ? (int) Math.Clamp(Math.Ceiling(retryAfter.Value.TotalSeconds), 0, int.MaxValue)
            : null;
        return new AccountNoticeResult(new AccountNotice(
            "Too many registration attempts",
            "Your last request was not processed. Wait before reloading the registration page to try again.",
            RetryAfterSeconds: seconds), StatusCodes.Status429TooManyRequests);
    }

    public static AccountNoticeResult HttpsRequired() => new(new AccountNotice(
        "Use a secure connection",
        "Registration requires HTTPS. Open the secure registration link from the setup guide before entering account details.",
        AllowReload: false), StatusCodes.Status403Forbidden);

    public Task ExecuteAsync(HttpContext context) => ExecuteResultAsync(
        new ActionContext(context, context.GetRouteData(), new ActionDescriptor()));

    public override Task ExecuteResultAsync(ActionContext context) {
        context.HttpContext.Response.Headers.CacheControl = "no-store,no-cache";
        context.HttpContext.Response.Headers.Pragma = "no-cache";
        if (ViewData.Model is AccountNotice { RetryAfterSeconds: int seconds }) {
            context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        }
        return base.ExecuteResultAsync(context);
    }
}
