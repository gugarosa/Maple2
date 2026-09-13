using System;
using Maple2.Database.Storage;
using Maple2.Model.Validators;
using Maple2.Server.Web.Filters;
using Maple2.Server.Web.Helpers;
using Maple2.Server.Web.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Maple2.Server.Web.Controllers;

[Route("account")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[AccountPage]
public sealed class AccountController(GameStorage storage) : Controller {
    [HttpGet("")]
    public IActionResult Index() {
        return View("Register", new RegistrationForm {
            RegisteredUsername = TempData["RegisteredUsername"] as string,
        });
    }

    [HttpPost("register")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(AccountPageProtection.RATE_LIMIT_POLICY)]
    [RequestSizeLimit(4096)]
    public IActionResult Register([FromForm] RegistrationForm form) {
        string username = AccountCredentialValidator.NormalizeUsername(form.Username);
        if (!AccountCredentialValidator.ValidRegistrationUsername(username)) {
            ModelState.AddModelError(nameof(form.Username), RegistrationResponses.USERNAME_REQUIREMENT);
        }
        if (!AccountCredentialValidator.ValidRegistrationPassword(form.Password)) {
            ModelState.AddModelError(nameof(form.Password), "Use 8-16 characters for your password, matching the game client's input limit.");
        }
        if (!string.Equals(form.Password, form.ConfirmPassword, StringComparison.Ordinal)) {
            ModelState.AddModelError(nameof(form.ConfirmPassword), "The passwords do not match.");
        }
        if (!ModelState.IsValid) {
            return RegistrationResponses.Invalid(this, username);
        }

        using GameStorage.Request db = storage.Context();
        AccountRegistrationResult result = db.RegisterAccount(username, form.Password);
        if (result == AccountRegistrationResult.Registered) {
            TempData["RegisteredUsername"] = username;
            return RedirectToAction(nameof(Index));
        }

        return RegistrationResponses.Rejected(this, username, result);
    }
}
