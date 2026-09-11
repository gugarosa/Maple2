using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Maple2.Server.Web.Model;

public sealed class RegistrationForm {
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string ConfirmPassword { get; set; } = string.Empty;

    [BindNever]
    public string? RegisteredUsername { get; init; }
}
