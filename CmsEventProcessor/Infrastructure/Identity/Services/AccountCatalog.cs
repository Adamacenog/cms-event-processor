using CmsEventProcessor.Application.Identity.Contracts;
using CmsEventProcessor.Application.Identity.Models;
using CmsEventProcessor.Infrastructure.Identity.Models;
using CmsEventProcessor.Infrastructure.Identity.Options;
using Microsoft.Extensions.Options;

namespace CmsEventProcessor.Infrastructure.Identity.Services;

/// <summary>
/// Authenticates against an immutable startup snapshot with separate CMS and consumer boundaries.
/// </summary>
internal sealed class AccountCatalog : IAccountAuthenticator
{
    private readonly AccountDefinition[] accounts;
    private readonly PasswordHashService passwords;
    private readonly AccountDefinition dummy;

    /// <summary>
    /// Snapshots the validated identities and prepares a dummy hash for unknown-user checks.
    /// </summary>
    /// <param name="settings">Validated authentication options whose accounts are copied into a startup snapshot.</param>
    /// <param name="passwords">The service used for configured and dummy-account password checks.</param>
    /// <exception cref="Microsoft.Extensions.Options.OptionsValidationException">The supplied options fail authentication configuration validation.</exception>
    public AccountCatalog(IOptions<AccountSettings> settings, PasswordHashService passwords)
    {
        // IOptions (not IOptionsMonitor) intentionally takes one snapshot until restart.
        accounts = settings.Value.Accounts.ToArray();
        this.passwords = passwords;
        var placeholder = new AccountDefinition { Username = "unknown-user", Kind = AccountKind.Reader };
        dummy = placeholder with { PasswordHash = passwords.Hash(placeholder, Guid.NewGuid().ToString("D")) };
    }

    /// <summary>
    /// Returns a hash-free identity only when the username, password, and trust boundary match.
    /// </summary>
    /// <param name="scope">The CMS or consumer trust boundary; unknown values fail closed.</param>
    /// <param name="username">The supplied username, compared ordinally and case-sensitively.</param>
    /// <param name="password">The supplied plaintext password, used only for verification.</param>
    /// <returns>The verified username and kind without its stored hash, or null when credentials or scope do not match.</returns>
    /// <exception cref="System.ArgumentNullException">The supplied password is null.</exception>
    public AccountIdentity? Authenticate(AccountScope scope, string username, string password)
    {
        var account = Array.Find(accounts, candidate =>
            string.Equals(candidate.Username, username, StringComparison.Ordinal) &&
            (scope == AccountScope.Cms && candidate.Kind == AccountKind.Cms ||
                scope == AccountScope.Consumer && candidate.Kind is AccountKind.Reader or AccountKind.Admin));

        // Unknown users and credentials for the other scheme still perform a password hash check.
        var verified = passwords.Verify(account ?? dummy, password);
        return account is not null && verified ? new AccountIdentity(account.Username, account.Kind) : null;
    }
}
