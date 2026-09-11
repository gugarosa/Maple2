using System.Text;

namespace Maple2.Model.Validators;

public static class AccountCredentialValidator {
    public const int MaxPasswordBytes = 72;
    // The v12 client's editable field truncates input after 16 UTF-16 code units.
    public const int MaxRegistrationPasswordLength = 16;

    public static string NormalizeUsername(string? username) => username?.Trim().ToLowerInvariant() ?? string.Empty;

    public static bool ValidRegistrationUsername(string username) {
        return username.Length is >= 3 and <= 24 &&
               username.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    }

    public static bool ValidLoginUsername(string username) {
        return !string.IsNullOrWhiteSpace(username) && username.Length <= 255 && !username.Any(char.IsControl);
    }

    public static bool ValidLoginPassword(string? password) {
        return !string.IsNullOrWhiteSpace(password) && password.Length <= MaxPasswordBytes &&
               !password.Contains('\0') && Encoding.UTF8.GetByteCount(password) <= MaxPasswordBytes;
    }

    public static bool ValidRegistrationPassword(string? password) {
        return ValidLoginPassword(password) && password!.Length <= MaxRegistrationPasswordLength &&
               password.EnumerateRunes().Count() >= 8;
    }
}
