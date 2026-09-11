using System;
using Maple2.Database.Storage;
using Maple2.Model.Validators;
using Maple2.Server.Web.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Maple2.Server.Web.Controllers;

[Route("account")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AccountController(GameStorage storage) : Controller {
    [HttpGet("")]
    public IActionResult Index() {
        return View("Register", new RegistrationForm {
            RegisteredUsername = TempData["RegisteredUsername"] as string,
        });
    }

    [HttpPost("register")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("account-registration")]
    [RequestSizeLimit(4096)]
    public IActionResult Register([FromForm] RegistrationForm form) {
        string username = AccountCredentialValidator.NormalizeUsername(form.Username);
        if (!AccountCredentialValidator.ValidRegistrationUsername(username)) {
            ModelState.AddModelError(nameof(form.Username), "Use 3-24 letters, numbers, or underscores for your username.");
        }
        if (!AccountCredentialValidator.ValidRegistrationPassword(form.Password)) {
            ModelState.AddModelError(nameof(form.Password), "Use 8-16 characters for your password, matching the game client's input limit.");
        }
        if (!string.Equals(form.Password, form.ConfirmPassword, StringComparison.Ordinal)) {
            ModelState.AddModelError(nameof(form.ConfirmPassword), "The passwords do not match.");
        }
        if (!ModelState.IsValid) {
            return Rejected(username, StatusCodes.Status400BadRequest);
        }

        using GameStorage.Request db = storage.Context();
        AccountRegistrationResult result = db.RegisterAccount(username, form.Password);
        if (result == AccountRegistrationResult.Registered) {
            TempData["RegisteredUsername"] = username;
            return RedirectToAction(nameof(Index));
        }

        ModelState.AddModelError(string.Empty, result switch {
            AccountRegistrationResult.UsernameTaken => "That username is already registered. Choose another username.",
            AccountRegistrationResult.InvalidUsername => "The username is not valid.",
            AccountRegistrationResult.InvalidPassword => "The password does not meet the requirements.",
            _ => "Registration could not be saved. Please try again.",
        });
        return Rejected(username, result switch {
            AccountRegistrationResult.UsernameTaken => StatusCodes.Status409Conflict,
            AccountRegistrationResult.Failed => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest,
        });
    }

    private IActionResult Rejected(string username, int statusCode) {
        Response.StatusCode = statusCode;
        return View("Register", new RegistrationForm { Username = username });
    }
}
