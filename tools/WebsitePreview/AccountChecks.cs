using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Maple2.Database.Storage;
using Maple2.Model.Validators;
using Maple2.Server.Web.Controllers;
using Maple2.Server.Web.Filters;
using Maple2.Server.Web.Helpers;
using Maple2.Server.Web.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.RateLimiting;

namespace WebsitePreview;

public sealed class AccountChecks(HttpClient client, Uri insecureOrigin, string root, string output, FixtureCalls calls) {
    public const string PlayerWebsite = "https://ms2.mapletime.dev/#getting-started";
    private readonly List<object> evidence = [];
    private readonly List<object> probes = [];
    private int assertions;

    public async Task RunAsync() {
        Directory.CreateDirectory(output);
        CheckProductionContracts();
        CheckCredentialBoundaries();
        CheckRejectionHelpers();

        string sourceTheme = await File.ReadAllTextAsync(Path.Combine(root, "website", "theme.css"));
        Check(PlayerTheme.Css == sourceTheme, "Compiled CSS must match the single shared theme source.");
        Check(ReferenceEquals(PlayerTheme.Css, PlayerTheme.Css), "The trusted theme resource must be cached.");

        string initial = await CaptureAsync("initial", 200);
        CheckForm(initial, string.Empty);
        CheckFields(initial, []);
        Check(!initial.Contains("id=\"registration-errors\"") && !initial.Contains("role=\"alert\""),
            "The initial GET fixture must not display an empty validation error or take correction focus.");
        Check(File.ReadAllText(Path.Combine(root, "deploy", "azure", "compose.application.yml")).Contains(PlayerWebsite),
            "The fixture setup destination must match the deployed application configuration.");
        Check(!initial.Contains("is available", StringComparison.OrdinalIgnoreCase), "Initial state must not claim username availability.");

        string errors = await CaptureAsync("errors", 400);
        CheckForm(errors, "x");
        CheckFields(errors, ["username", "password", "confirm-password"]);
        CheckSummary(errors);

        string conflict = await CaptureAsync("conflict", 409);
        CheckForm(conflict, "preview_tester");
        CheckFields(conflict, ["username"]);
        CheckSummary(conflict);
        Check(conflict.Contains("Choose another username."), "A conflict must explain the next action.");

        string unavailable = await CaptureAsync("unavailable", 503);
        CheckForm(unavailable, "preview_tester");
        CheckFields(unavailable, []);
        CheckSummary(unavailable);
        Check(unavailable.Contains("try again later", StringComparison.OrdinalIgnoreCase), "Service failure needs an honest retry action.");
        Check(!unavailable.Contains("Retry-After", StringComparison.OrdinalIgnoreCase), "Service failure must not invent a retry delay.");

        string invalidUsername = await CaptureAsync("invalid-username", 400);
        CheckForm(invalidUsername, "x");
        CheckFields(invalidUsername, ["username"]);
        string invalidPassword = await CaptureAsync("invalid-password", 400);
        CheckForm(invalidPassword, "preview_tester");
        CheckFields(invalidPassword, ["password"]);

        string success = await CaptureAsync("success", 200);
        CheckSuccess(success, "preview_tester");
        string longSuccess = await CaptureAsync("long-success", 200);
        CheckSuccess(longSuccess, AccountFixtureController.LongText);
        Check(!longSuccess.Contains("<script>"), "Registered usernames must be encoded.");
        string longErrors = await CaptureAsync("long-errors", 400);
        CheckForm(longErrors, AccountFixtureController.LongText);
        CheckFields(longErrors, ["username"]);
        CheckSummary(longErrors);
        Check(!longErrors.Contains("<script>"), "ModelState messages and usernames must be encoded.");

        string exception = await CaptureAsync("exception-error", 400);
        CheckSummary(exception);
        Check(!exception.Contains(AccountFixtureController.HiddenServerDetail), "ModelState exceptions must not reveal server details.");
        Check(exception.Contains("Check your details and try again."), "Empty error messages need a safe fallback.");

        string expired = await CaptureAsync("expired", 400);
        CheckNotice(expired, true);
        string throttledWithoutDelay = await CaptureAsync("throttled-no-retry", 429);
        CheckNotice(throttledWithoutDelay, true);
        Check(!Regex.IsMatch(throttledWithoutDelay, @"\b\d+ seconds?\b"), "No Retry-After metadata means no invented delay.");

        await CheckAntiforgeryAsync(initial);
        await CheckRateLimiterAsync();
        client.DefaultRequestHeaders.Remove("X-Forwarded-Proto");
        try {
            using (HttpResponseMessage blocked = await client.GetAsync(new Uri(insecureOrigin, "/account/fixture/initial"))) {
                string html = await SaveAsync("https-required", "/account/fixture/initial (HTTP, no HTTPS proxy header)", blocked, 403);
                CheckNotice(html, false);
                Check(!html.Contains("Reload registration"), "An HTTPS block must not suggest reloading an insecure form.");
            }
            using (HttpResponseMessage blockedPost = await client.PostAsync("account/fixture/verify", new FormUrlEncodedContent([]))) {
                Check(blockedPost.StatusCode == HttpStatusCode.Forbidden, "Non-HTTPS POSTs must be blocked before antiforgery or an action runs.");
                CheckNotice(await blockedPost.Content.ReadAsStringAsync(), false);
                Check(calls.VerifiedForms == 1, "The HTTPS guard must not allow an insecure POST to execute.");
            }
            using (HttpResponseMessage outside = await client.GetAsync(new Uri(insecureOrigin, "/fixture-outside"))) {
                Check(outside.StatusCode == HttpStatusCode.NoContent, "The HTTPS guard must not affect unrelated routes.");
            }
        } finally {
            client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        }
        using (HttpResponseMessage outside = await client.PostAsync("fixture-outside/verify", new FormUrlEncodedContent([]))) {
            Check(outside.StatusCode == HttpStatusCode.BadRequest, "Unrelated antiforgery checks still reject invalid requests.");
            Check(!(await outside.Content.ReadAsStringAsync()).Contains("MapleTime"), "Account result filters must not transform unrelated responses.");
        }
        using (HttpResponseMessage unrelatedError = await client.GetAsync("account/fixture/bad-request")) {
            Check(unrelatedError.StatusCode == HttpStatusCode.BadRequest, "Unrelated account errors must retain their status.");
            Check(!(await unrelatedError.Content.ReadAsStringAsync()).Contains("This form could not be verified"),
                "The account filter must not relabel arbitrary failures as antiforgery errors.");
        }

        Environment.SetEnvironmentVariable("PLAYER_WEBSITE_URL", null);
        try {
            string noGuide = await CaptureAsync("success", 200, "success-no-guide");
            Check(!Regex.IsMatch(noGuide, @"<(form|input)\b"), "Success without a guide must not offer another credential form.");
            Check(noGuide.Contains("Ask your server operator"), "Missing optional guide configuration needs useful fallback instructions.");
            Check(!noGuide.Contains("Continue to client setup"), "Missing optional guide configuration must not create a broken primary link.");
            Check(noGuide.Contains($"<code>{Maple2.Server.Core.Constants.Target.LoginIp}</code>") &&
                  noGuide.Contains($"<code>{Maple2.Server.Core.Constants.Target.LoginPort}</code>"),
                "The existing configured Login host and port must remain available when no setup website is configured.");
            Check(noGuide.Contains("Leave automatic login off") && noGuide.Contains("Register another account"),
                "The no-guide success state must retain connection instructions and the secondary registration route.");
        } finally {
            Environment.SetEnvironmentVariable("PLAYER_WEBSITE_URL", PlayerWebsite);
        }

        await File.WriteAllTextAsync(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(new {
            generatedUtc = DateTimeOffset.UtcNow,
            runtime = Environment.Version.ToString(),
            assertions,
            source = "Compiled Razor items from Maple2.Server.Web; no runtime compilation or hand-authored HTML fixtures.",
            themeSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PlayerTheme.Css))),
            playerWebsiteUrl = PlayerWebsite,
            limits = "Loopback HTTP exercises Web's trusted proxy/HTTPS-header path. No TLS acceptance, production controller, GameStorage, database, real registration, browser or deployment acceptance.",
            fixtures = evidence,
            probes,
        }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        Console.WriteLine($"Account UI: {assertions} assertions passed; {evidence.Count} compiled Razor fixtures saved to {output}");
        Console.WriteLine("Only loopback fixture requests were used. The host is stopping; no production account or database was accessed.");
    }

    private async Task<string> CaptureAsync(string state, int status, string? fileName = null) {
        string path = "account/fixture/" + state;
        using HttpResponseMessage response = await client.GetAsync(path);
        return await SaveAsync(fileName ?? state, "/" + path, response, status);
    }

    private async Task<string> SaveAsync(string name, string route, HttpResponseMessage response, int status) {
        string html = await response.Content.ReadAsStringAsync();
        Check((int) response.StatusCode == status, $"{name}: expected HTTP {status}, got {(int) response.StatusCode}.");
        Check(response.Content.Headers.ContentType?.MediaType == "text/html", $"{name}: compiled views must return HTML.");
        Check(response.Headers.CacheControl?.NoStore == true, $"{name}: responses must be no-store.");
        Check(response.Headers.CacheControl?.NoCache == true, $"{name}: responses must be no-cache.");
        if (name is "throttled-no-retry" or "unavailable") {
            Check(response.Headers.RetryAfter == null, $"{name}: no retry metadata means no invented Retry-After header.");
        }
        if (name == "initial") {
            Check(response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies) &&
                  cookies.Any(cookie => cookie.Contains("; secure", StringComparison.OrdinalIgnoreCase) &&
                                        cookie.Contains("; httponly", StringComparison.OrdinalIgnoreCase)),
                "The HTTPS-proxied registration form must issue a Secure, HttpOnly antiforgery cookie.");
        }
        CheckDocument(html, name);
        string file = name + ".html";
        await File.WriteAllTextAsync(Path.Combine(output, file), html, new UTF8Encoding(false));
        evidence.Add(new {
            state = name,
            route,
            status,
            file,
            contentType = response.Content.Headers.ContentType?.ToString(),
            cacheControl = response.Headers.CacheControl?.ToString(),
            retryAfter = response.Headers.RetryAfter?.ToString(),
            sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(html))),
        });
        return html;
    }

    private void CheckDocument(string html, string state) {
        Check(Regex.Matches(html, @"<h1\b").Count == 1, $"{state}: exactly one page heading.");
        Check(html.Contains("<html lang=\"en\">"), $"{state}: language must be declared.");
        Check(html.Contains("name=\"viewport\""), $"{state}: viewport must be responsive.");
        Check(!Regex.IsMatch(html, @"<(script|link|img|iframe)\b", RegexOptions.IgnoreCase), $"{state}: no scripts or external asset dependencies.");
        Check(!Regex.IsMatch(html, @"@import\b|\burl\s*\(", RegexOptions.IgnoreCase), $"{state}: no CSS downloads.");
        Check(!html.Contains(AccountFixtureController.PasswordSentinel), $"{state}: password values must never render.");
        string css = Regex.Match(html, "<style id=\"player-theme\">(.*?)</style>", RegexOptions.Singleline).Groups[1].Value;
        Check(css == PlayerTheme.Css, $"{state}: shared tokens must come from the exact embedded CSS resource.");

        string[] ids = Regex.Matches(html, @"\bid=""([^""]+)""").Select(match => match.Groups[1].Value).ToArray();
        Check(ids.Distinct(StringComparer.Ordinal).Count() == ids.Length, $"{state}: IDs must be unique.");
        foreach (Match reference in Regex.Matches(html, @"\baria-(?:describedby|labelledby)=""([^""]+)""")) {
            foreach (string id in reference.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
                Check(ids.Contains(id), $"{state}: ARIA reference {id} needs a real target.");
            }
        }
        foreach (Match anchor in Regex.Matches(html, @"<a\b[^>]*>", RegexOptions.IgnoreCase)) {
            string href = Attribute(anchor.Value, "href") ?? string.Empty;
            if (href.StartsWith('#')) {
                Check(ids.Contains(href[1..]), $"{state}: fragment {href} needs a real target.");
            } else {
                Check(href.StartsWith("https://", StringComparison.Ordinal) ||
                      (href.StartsWith('/') && !href.StartsWith("//", StringComparison.Ordinal)),
                    $"{state}: recovery and navigation links must be local paths or HTTPS.");
            }
        }
    }

    private void CheckForm(string html, string username) {
        string form = Regex.Match(html, @"<form\b[^>]*>").Value;
        Check(Attribute(form, "method") == "post" && Attribute(form, "action") == "/account/register", "Registration keeps the existing POST route.");
        Check(Attribute(Input(html, "username"), "value") == username, "Rejected usernames must be preserved exactly after normalization.");
        Check(Attribute(Input(html, "username"), "minlength") == "3" &&
              Attribute(Input(html, "username"), "maxlength") == "24" &&
              Attribute(Input(html, "username"), "pattern") == "[A-Za-z0-9_]{3,24}", "Username constraints must remain 3-24 ASCII letters, numbers or underscores.");
        foreach (string id in new[] { "username", "password", "confirm-password" }) {
            string input = Input(html, id);
            Check(html.Contains($"for=\"{id}\""), $"{id}: persistent label must be associated.");
            Check(Regex.IsMatch(input, @"\brequired(?:\s|/?>)"), $"{id}: native required validation must remain.");
            if (id != "username") {
                Check(Attribute(input, "type") == "password", $"{id}: password must stay obscured.");
                Check(string.IsNullOrEmpty(Attribute(input, "value")), $"{id}: passwords must be cleared.");
                Check(Attribute(input, "minlength") == "8" && Attribute(input, "maxlength") == "16", $"{id}: preserve 8-16 new-password limits.");
                Check(Attribute(input, "autocomplete") == "new-password", $"{id}: support password managers.");
            }
        }
        Check(!string.IsNullOrEmpty(AntiforgeryToken(html)), "Registration must include a real antiforgery token.");
        Check(Regex.IsMatch(html, @"<button\b[^>]*class=""button account-primary""[^>]*type=""submit"""), "Create account must remain a full-width native submit button.");
    }

    private void CheckFields(string html, string[] invalidFields) {
        foreach (string id in new[] { "username", "password", "confirm-password" }) {
            string input = Input(html, id);
            bool invalid = invalidFields.Contains(id);
            Check((Attribute(input, "aria-invalid") == "true") == invalid, $"{id}: aria-invalid must match real field errors, not a global error.");
            if (invalid) {
                Check((Attribute(input, "aria-describedby") ?? string.Empty).Split(' ').Contains(id + "-error"), $"{id}: inline error must describe its field.");
                Check(html.Contains($"href=\"#{id}\""), $"{id}: error summary must link to the affected field.");
                Check(html.Contains($"id=\"{id}-error\""), $"{id}: inline error must be rendered.");
            }
        }
    }

    private void CheckSummary(string html) {
        string summary = Regex.Match(html, @"<section\b[^>]*id=""registration-errors""[^>]*>").Value;
        Check(Attribute(summary, "tabindex") == "-1" && summary.Contains("autofocus"), "Server errors need a focusable, initially focused summary without JavaScript.");
        Check(Attribute(summary, "role") == "alert", "Server errors need an accessible announcement.");
        Check(html.Contains("Re-enter both password fields"), "Password clearing needs an actionable explanation.");
        string section = Regex.Match(html, @"<section\b[^>]*id=""registration-errors""[\s\S]*?</section>").Value;
        int[] fieldOrder = Regex.Matches(section, @"href=""#(username|password|confirm-password)""")
            .Select(match => match.Groups[1].Value switch {
                "username" => 1,
                "password" => 2,
                _ => 3,
            }).ToArray();
        Check(fieldOrder.SequenceEqual(fieldOrder.OrderBy(value => value)),
            "Error recovery links must follow the visual order of the form fields.");
    }

    private void CheckSuccess(string html, string username) {
        Check(!Regex.IsMatch(html, @"<(form|input)\b"), "Success must not offer a new registration form.");
        Check(WebUtility.HtmlDecode(html).Contains(username), "Success must confirm only the username supplied by the completed registration.");
        string primary = Regex.Match(html, @"<a\b[^>]*class=""button account-primary""[^>]*>").Value;
        Check(Attribute(primary, "href") == PlayerWebsite, "Success's primary action must use PLAYER_WEBSITE_URL.");
        Check(html.Contains("Continue to client setup"), "Success must lead into client setup.");
        Check(Regex.IsMatch(html, @"<a\b[^>]*class=""text-link""[^>]*href=""/account""[^>]*>Register another account</a>"),
            "Another registration must remain a subordinate text link, not replace the client-setup action.");
        Check(html.IndexOf("Continue to client setup", StringComparison.Ordinal) <
              html.IndexOf("Register another account", StringComparison.Ordinal),
            "Client setup must precede the optional registration route.");
    }

    private void CheckNotice(string html, bool canReload) {
        Check(!Regex.IsMatch(html, @"<(form|input)\b"), "Blocked requests must never render a credential form or token.");
        string notice = Regex.Match(html, @"<section\b[^>]*class=""account-notice""[^>]*>").Value;
        Check(Attribute(notice, "tabindex") == "-1" && notice.Contains("autofocus"), "Recovery notices must be focusable without JavaScript.");
        Check(html.Contains("href=\"/account\"") == canReload, "Only eligible notices may offer a local registration reload.");
        Check(html.Contains($"href=\"{PlayerWebsite}\""), "Recovery notices must preserve the configured HTTPS setup guide.");
    }

    private async Task CheckAntiforgeryAsync(string initial) {
        using (HttpResponseMessage missing = await client.PostAsync("account/fixture/verify", new FormUrlEncodedContent([]))) {
            string html = await SaveAsync("expired-post", "/account/fixture/verify (inert POST, missing token)", missing, 400);
            CheckNotice(html, true);
            Check(calls.VerifiedForms == 0, "Missing antiforgery tokens must short-circuit before the inert action.");
        }
        using (HttpResponseMessage invalid = await client.PostAsync("account/fixture/verify",
                   new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = "not-a-valid-fixture-token" }))) {
            Check(invalid.StatusCode == HttpStatusCode.BadRequest, "Invalid antiforgery tokens must remain HTTP 400.");
            Check((await invalid.Content.ReadAsStringAsync()).Contains("This form could not be verified"), "Real antiforgery failures need the account notice.");
            Check(calls.VerifiedForms == 0, "Invalid antiforgery tokens must not execute the action.");
        }
        string token = AntiforgeryToken(initial);
        using (HttpResponseMessage valid = await client.PostAsync("account/fixture/verify",
                   new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }))) {
            Check(valid.StatusCode == HttpStatusCode.NoContent && calls.VerifiedForms == 1, "A valid token/cookie pair must still pass the inert antiforgery probe.");
        }
        using (HttpResponseMessage binding = await client.PostAsync("account/fixture/binding",
                   new FormUrlEncodedContent(new Dictionary<string, string> {
                       ["__RequestVerificationToken"] = token,
                       ["Username"] = "preview_tester",
                       ["RegisteredUsername"] = "forged_success",
                   }))) {
            Check(binding.StatusCode == HttpStatusCode.OK, "The inert model-binding probe must run with a valid token.");
            using JsonDocument body = JsonDocument.Parse(await binding.Content.ReadAsStringAsync());
            Check(body.RootElement.GetProperty("username").GetString() == "preview_tester", "Ordinary form input must still bind.");
            Check(body.RootElement.GetProperty("registeredUsername").ValueKind == JsonValueKind.Null,
                "Real MVC binding must ignore a posted RegisteredUsername success claim.");
        }
        using (HttpResponseMessage oversized = await client.PostAsync("account/fixture/verify",
                   new FormUrlEncodedContent(new Dictionary<string, string> {
                       ["__RequestVerificationToken"] = token,
                       ["fixturePadding"] = new string('x', 5000),
                   }))) {
            Check(oversized.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge,
                "Oversized requests must be rejected by the existing MVC/antiforgery request-size boundary.");
            Check(calls.VerifiedForms == 1, "Oversized requests must never reach the inert action.");
            probes.Add(new {
                name = "oversized-inert-post",
                route = "/account/fixture/verify",
                status = (int) oversized.StatusCode,
                reachedAction = false,
            });
        }
    }

    private async Task CheckRateLimiterAsync() {
        for (int attempt = 0; attempt < 5; attempt++) {
            using HttpResponseMessage allowed = await client.GetAsync("account/fixture/throttled");
            Check(allowed.StatusCode == HttpStatusCode.NoContent, "The existing fixed-window limiter must allow its five permits.");
        }
        using (HttpResponseMessage blocked = await client.GetAsync("account/fixture/throttled")) {
            string html = await SaveAsync("throttled", "/account/fixture/throttled (sixth inert GET)", blocked, 429);
            CheckNotice(html, true);
            TimeSpan? retry = blocked.Headers.RetryAfter?.Delta;
            Check(retry.HasValue && retry.Value.TotalSeconds is > 0 and <= 60, "Retry-After must come from the real one-minute fixed-window lease.");
            string seconds = ((int) retry!.Value.TotalSeconds).ToString();
            Check(html.Contains($"Wait at least {seconds} seconds") || (seconds == "1" && html.Contains("Wait at least 1 second")),
                "The static notice delay must match the real Retry-After header.");
        }
        using HttpResponseMessage outside = await client.GetAsync("fixture-outside/throttled");
        Check(outside.StatusCode == HttpStatusCode.TooManyRequests, "The shared limiter must still reject an exhausted unrelated fixture partition.");
        Check(outside.Content.Headers.ContentType?.MediaType == "text/plain", "Account HTML must not replace unrelated rate-limit responses.");
        Check(!(await outside.Content.ReadAsStringAsync()).Contains("MapleTime"), "Rate-limit notice scope must stay account-only.");
    }

    private void CheckProductionContracts() {
        Type account = typeof(AccountController);
        Check(account.GetCustomAttribute<RouteAttribute>()?.Template == "account", "Production registration keeps its route.");
        Check(account.GetCustomAttribute<ResponseCacheAttribute>() is { NoStore: true, Location: ResponseCacheLocation.None }, "Production account responses must remain no-store.");
        Check(account.GetCustomAttribute<AccountPageAttribute>() != null, "The account-only always-run result filter must be installed.");
        MethodInfo register = account.GetMethod(nameof(AccountController.Register))!;
        Check(register.GetCustomAttribute<HttpPostAttribute>()?.Template == "register", "The production POST endpoint must not change.");
        Check(register.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>() != null, "Production antiforgery must remain enabled.");
        Check(register.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName == AccountPageProtection.RATE_LIMIT_POLICY, "Production registration must use the tested limiter.");
        Check(register.GetCustomAttributesData().Single(attribute => attribute.AttributeType == typeof(RequestSizeLimitAttribute))
            .ConstructorArguments[0].Value is 4096L, "Production request-size limit must remain 4096 bytes.");
        Check(typeof(RegistrationForm).GetProperty(nameof(RegistrationForm.RegisteredUsername))!.GetCustomAttribute<BindNeverAttribute>() != null,
            "Posted input must not be able to claim successful registration.");

        string controller = File.ReadAllText(Path.Combine(root, "Maple2.Server.Web", "Controllers", "AccountController.cs"));
        Check(Regex.IsMatch(controller, @"TempData\[""RegisteredUsername""\]\s*=\s*username;\s*return RedirectToAction\(nameof\(Index\)\);"),
            "Successful production creation must retain TempData and POST/Redirect/GET.");
        Check(controller.Contains("RegisteredUsername = TempData[\"RegisteredUsername\"] as string"), "Success must still be consumed on the subsequent GET.");
        Check(controller.Contains("db.RegisterAccount(username, form.Password)"), "Production registration must pass the original password without truncation.");
        string program = File.ReadAllText(Path.Combine(root, "Maple2.Server.Web", "Program.cs"));
        Check(program.Contains("app.Use(AccountPageProtection.RequireHttps)") && program.Contains("if (requireHttpsRegistration)"),
            "Production must retain its configured HTTPS boundary using the tested guard.");
        Check(program.IndexOf("app.UseForwardedHeaders()", StringComparison.Ordinal) < program.IndexOf("app.Use(AccountPageProtection.RequireHttps)", StringComparison.Ordinal),
            "Trusted forwarded-header handling must still precede the HTTPS guard.");
        Check(program.Contains("uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0"), "Configured player/source links must retain HTTPS and embedded-credential validation.");
        Check(program.Contains("CookieSecurePolicy.SameAsRequest"), "Antiforgery cookies must retain the existing secure policy.");
    }

    private void CheckCredentialBoundaries() {
        Check(AccountCredentialValidator.NormalizeUsername(" Preview_Tester ") == "preview_tester", "Username normalization must remain explicit and invariant.");
        foreach ((string username, bool valid) in new[] {
                     ("ab", false), ("abc", true), (new string('a', 24), true), (new string('a', 25), false),
                     ("player_1", true), ("player name", false), ("漢字名", false), ("player\n", false),
                 }) {
            Check(AccountCredentialValidator.ValidRegistrationUsername(username) == valid, "Username boundary regression.");
        }
        foreach ((string password, bool valid) in new[] {
                     ("1234567", false), ("12345678", true), (new string('x', 16), true), (new string('x', 17), false),
                     ("        ", false), ("secret\0value", false), (" exact password ", true),
                     (string.Concat(Enumerable.Repeat("🌿", 8)), true), (string.Concat(Enumerable.Repeat("🌿", 4)), false),
                 }) {
            Check(AccountCredentialValidator.ValidRegistrationPassword(password) == valid, "Password boundary regression.");
        }
        Check(AccountCredentialValidator.ValidLoginPassword(new string('x', 72)), "New-password limits must not tighten existing login semantics.");
    }

    private void CheckRejectionHelpers() {
        AccountFixtureController controller = new(new FixtureCalls()) {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        controller.ModelState.SetModelValue("form.Password", AccountFixtureController.PasswordSentinel, AccountFixtureController.PasswordSentinel);
        controller.ModelState.AddModelError("form.Password", "Keep this field error.");
        controller.ModelState.SetModelValue("ConfirmPassword", AccountFixtureController.PasswordSentinel, AccountFixtureController.PasswordSentinel);
        ViewResult rejected = RegistrationResponses.Rejected(controller, "preview_tester", AccountRegistrationResult.UsernameTaken);
        Check(controller.Response.StatusCode == 409, "Conflict mapping must retain HTTP 409.");
        Check(rejected.Model is RegistrationForm { Username: "preview_tester", Password: "", ConfirmPassword: "", RegisteredUsername: null },
            "Rejections must create a credential-free model without a false success state.");
        Check(controller.ModelState["form.Password"]!.AttemptedValue == string.Empty &&
              controller.ModelState["ConfirmPassword"]!.AttemptedValue == string.Empty, "All password ModelState values, including prefixed keys, must be cleared.");
        Check(controller.ModelState["form.Password"]!.Errors.Count == 1, "Clearing password values must preserve field errors.");
        bool refusedSuccess = false;
        try {
            RegistrationResponses.Rejected(controller, "preview_tester", AccountRegistrationResult.Registered);
        } catch (ArgumentOutOfRangeException) {
            refusedSuccess = true;
        }
        Check(refusedSuccess, "The rejection helper must never silently treat success as an error state.");
    }

    private void Check(bool condition, string message) {
        if (!condition) {
            throw new InvalidOperationException(message);
        }
        assertions++;
    }

    private static string Input(string html, string id) =>
        Regex.Matches(html, @"<input\b[^>]*>").Select(match => match.Value).Single(tag => Attribute(tag, "id") == id);

    private static string AntiforgeryToken(string html) =>
        Attribute(Regex.Matches(html, @"<input\b[^>]*>").Select(match => match.Value)
            .Single(tag => Attribute(tag, "name") == "__RequestVerificationToken"), "value") ?? string.Empty;

    private static string? Attribute(string tag, string name) {
        Match match = Regex.Match(tag, $@"(?:^|\s){Regex.Escape(name)}=""([^""]*)""");
        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : null;
    }
}
