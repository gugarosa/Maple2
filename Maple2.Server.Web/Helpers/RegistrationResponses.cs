using System;
using System.Linq;
using Maple2.Database.Storage;
using Maple2.Server.Web.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Maple2.Server.Web.Helpers;

public static class RegistrationResponses {
    public const string USERNAME_REQUIREMENT = "Use 3-24 letters (A-Z), numbers (0-9), or underscores.";

    public static ViewResult Invalid(Controller controller, string username) =>
        Render(controller, username, StatusCodes.Status400BadRequest);

    public static ViewResult Rejected(Controller controller, string username, AccountRegistrationResult result) {
        (string field, string message, int statusCode) = result switch {
            AccountRegistrationResult.UsernameTaken => (nameof(RegistrationForm.Username),
                "That username is already registered. Choose another username.", StatusCodes.Status409Conflict),
            AccountRegistrationResult.InvalidUsername => (nameof(RegistrationForm.Username),
                USERNAME_REQUIREMENT, StatusCodes.Status400BadRequest),
            AccountRegistrationResult.InvalidPassword => (nameof(RegistrationForm.Password),
                "Use 8-16 characters for your password, matching the game client's input limit.", StatusCodes.Status400BadRequest),
            AccountRegistrationResult.Failed => (string.Empty,
                "We couldn't save your registration. Please try again later.", StatusCodes.Status503ServiceUnavailable),
            _ => throw new ArgumentOutOfRangeException(nameof(result), result, "Only rejected registrations can be rendered here."),
        };
        controller.ModelState.AddModelError(field, message);
        return Render(controller, username, statusCode);
    }

    private static ViewResult Render(Controller controller, string username, int statusCode) {
        foreach (string key in controller.ModelState.Keys.ToArray()) {
            string field = key[(key.LastIndexOf('.') + 1)..];
            if (field.Equals(nameof(RegistrationForm.Password), StringComparison.OrdinalIgnoreCase) ||
                field.Equals(nameof(RegistrationForm.ConfirmPassword), StringComparison.OrdinalIgnoreCase)) {
                controller.ModelState.SetModelValue(key, string.Empty, string.Empty);
            }
        }
        controller.Response.StatusCode = statusCode;
        return controller.View("~/Views/Account/Register.cshtml", new RegistrationForm { Username = username });
    }
}
