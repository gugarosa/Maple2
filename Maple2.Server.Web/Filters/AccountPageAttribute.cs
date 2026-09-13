using System;
using Maple2.Server.Web.ViewResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Maple2.Server.Web.Filters;

[AttributeUsage(AttributeTargets.Class)]
public sealed class AccountPageAttribute : Attribute, IAlwaysRunResultFilter {
    public void OnResultExecuting(ResultExecutingContext context) {
        context.HttpContext.Response.Headers.CacheControl = "no-store,no-cache";
        context.HttpContext.Response.Headers.Pragma = "no-cache";
        if (context.Result is AntiforgeryValidationFailedResult) {
            context.Result = AccountNoticeResult.Expired();
        }
    }

    public void OnResultExecuted(ResultExecutedContext context) { }
}
