using System.Data.Common;
using Maple2.Model.Enum;
using Maple2.Model.Game;
using Maple2.Model.Validators;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Maple2.Database.Storage;

public enum AccountRegistrationResult {
    Failed,
    Registered,
    InvalidUsername,
    InvalidPassword,
    UsernameTaken,
}

public partial class GameStorage {
    public partial class Request {
        public AccountRegistrationResult RegisterAccount(string username, string password) {
            if (HasFailed || IsTransaction) {
                Logger.LogWarning("Account registration requires a fresh request without an active transaction");
                return AccountRegistrationResult.Failed;
            }
            string normalized = AccountCredentialValidator.NormalizeUsername(username);
            if (!AccountCredentialValidator.ValidRegistrationUsername(normalized)) {
                Logger.LogWarning("Rejected account registration with an invalid username");
                return AccountRegistrationResult.InvalidUsername;
            }
            if (!AccountCredentialValidator.ValidRegistrationPassword(password)) {
                Logger.LogWarning("Rejected account registration with an invalid password");
                return AccountRegistrationResult.InvalidPassword;
            }
            try {
                if (Context.Account.Any(account => account.Username == normalized)) {
                    return AccountRegistrationResult.UsernameTaken;
                }
                CreateAccount(new Account {
                    Username = normalized,
                    AdminPermissions = AdminPermissions.None,
                }, password);
                return AccountRegistrationResult.Registered;
            } catch (DbUpdateException ex) when (ex.GetBaseException() is DbException { SqlState: "23000" }) {
                if (Context.Account.AsNoTracking().Any(account => account.Username == normalized)) {
                    Logger.LogInformation("Concurrent registration claimed username {Username}", normalized);
                    return AccountRegistrationResult.UsernameTaken;
                }
                Logger.LogError(ex, "Account registration violated a persistence constraint");
                return AccountRegistrationResult.Failed;
            } catch (Exception ex) when (ex is DbUpdateException || ex.GetBaseException() is DbException) {
                Logger.LogError(ex, "Account registration could not be persisted");
                return AccountRegistrationResult.Failed;
            }
        }
    }
}
