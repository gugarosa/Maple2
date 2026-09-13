namespace Maple2.Server.Web.Model;

public sealed record AccountNotice(string Title, string Description, bool AllowReload = true, int? RetryAfterSeconds = null);
