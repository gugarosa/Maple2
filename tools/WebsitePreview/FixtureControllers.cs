using Maple2.Database.Storage;
using Maple2.Model.Validators;
using Maple2.Server.Web.Filters;
using Maple2.Server.Web.Helpers;
using Maple2.Server.Web.Model;
using Maple2.Server.Web.ViewResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace WebsitePreview;

public sealed class FixtureCalls {
    public int VerifiedForms;
}

[Route("account/fixture")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[AccountPage]
public sealed class AccountFixtureController(FixtureCalls calls) : Controller {
    public const string PasswordSentinel = "PREVIEW_ONLY_PASSWORD_NOT_FOR_AN_ACCOUNT";
    public const string HiddenServerDetail = "FIXTURE_SERVER_DETAIL_MUST_NOT_LEAK";
    public static readonly string LongText = new string('x', 180) + " 漢字 العربية 🌿 <script>alert('fixture')</script>";

    [HttpGet("{state}")]
    public IActionResult State(string state) {
        ModelState.SetModelValue(nameof(RegistrationForm.Password), PasswordSentinel, PasswordSentinel);
        ModelState.SetModelValue(nameof(RegistrationForm.ConfirmPassword), PasswordSentinel, PasswordSentinel);
        ModelState.MarkFieldValid(nameof(RegistrationForm.Password));
        ModelState.MarkFieldValid(nameof(RegistrationForm.ConfirmPassword));
        switch (state) {
            case "initial":
                return Register(new RegistrationForm { Password = PasswordSentinel, ConfirmPassword = PasswordSentinel });
            case "errors":
                ModelState.AddModelError(nameof(RegistrationForm.Username), RegistrationResponses.USERNAME_REQUIREMENT);
                ModelState.AddModelError(nameof(RegistrationForm.Password), "Use 8-16 characters for your password, matching the game client's input limit.");
                ModelState.AddModelError(nameof(RegistrationForm.ConfirmPassword), "The passwords do not match.");
                return RegistrationResponses.Invalid(this, AccountCredentialValidator.NormalizeUsername(" X "));
            case "conflict":
                return RegistrationResponses.Rejected(this, "preview_tester", AccountRegistrationResult.UsernameTaken);
            case "unavailable":
                return RegistrationResponses.Rejected(this, "preview_tester", AccountRegistrationResult.Failed);
            case "invalid-username":
                return RegistrationResponses.Rejected(this, "x", AccountRegistrationResult.InvalidUsername);
            case "invalid-password":
                return RegistrationResponses.Rejected(this, "preview_tester", AccountRegistrationResult.InvalidPassword);
            case "success":
                return Register(new RegistrationForm { RegisteredUsername = "preview_tester" });
            case "long-errors":
                ModelState.AddModelError("form.Username", "Choose a supported username. " + LongText);
                return RegistrationResponses.Invalid(this, LongText);
            case "long-success":
                return Register(new RegistrationForm { RegisteredUsername = LongText });
            case "exception-error":
                ModelState.AddModelError(string.Empty, string.Empty);
                ModelState[string.Empty]!.Errors.Add(new InvalidOperationException(HiddenServerDetail));
                return RegistrationResponses.Invalid(this, "preview_tester");
            case "expired":
                return AccountNoticeResult.Expired();
            case "throttled-no-retry":
                return AccountNoticeResult.Throttled();
            default:
                return NotFound();
        }
    }

    [HttpGet("throttled")]
    [EnableRateLimiting(AccountPageProtection.RATE_LIMIT_POLICY)]
    public IActionResult Throttle() => NoContent();

    [HttpPost("verify")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(4096)]
    public IActionResult Verify() {
        Interlocked.Increment(ref calls.VerifiedForms);
        return NoContent();
    }

    [HttpPost("binding")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(4096)]
    public IActionResult Binding([FromForm] RegistrationForm form) => Json(new {
        form.Username,
        form.RegisteredUsername,
    });

    [HttpGet("bad-request")]
    public IActionResult BadRequestProbe() => BadRequest();

    private ViewResult Register(RegistrationForm form) => View("~/Views/Account/Register.cshtml", form);
}

[Route("fixture-outside")]
public sealed class OutsideFixtureController : Controller {
    [HttpGet("")]
    public IActionResult Index() => NoContent();

    [HttpPost("verify")]
    [ValidateAntiForgeryToken]
    public IActionResult Verify() => NoContent();

    [HttpGet("throttled")]
    [EnableRateLimiting(AccountPageProtection.RATE_LIMIT_POLICY)]
    public IActionResult Throttle() => NoContent();
}
