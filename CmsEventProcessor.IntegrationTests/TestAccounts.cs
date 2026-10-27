using CmsEventProcessor.Application.Identity.Models;
using CmsEventProcessor.Infrastructure.Identity.Models;
using CmsEventProcessor.Infrastructure.Identity.Services;

namespace CmsEventProcessor.IntegrationTests;

/// <summary>
/// Provides ephemeral test identities without reading or persisting developer credentials.
/// </summary>
internal static class TestAccounts
{
    public static readonly string CmsPassword = Guid.NewGuid().ToString("D");
    public static readonly string ReaderPassword = Guid.NewGuid().ToString("D");
    public static readonly string AdminPassword = Guid.NewGuid().ToString("D");
    public static readonly Dictionary<string, string?> Configuration = BuildConfiguration();

    /// <summary>
    /// Creates scheme-compatible account settings using newly salted password hashes.
    /// </summary>
    /// <returns>Configuration keys containing valid hashes and account kinds for the three ephemeral fixture identities.</returns>
    private static Dictionary<string, string?> BuildConfiguration()
    {
        var passwords = new PasswordHashService();
        var accounts = new[]
        {
            (Username: "cms-test0001", Kind: AccountKind.Cms, Password: CmsPassword),
            (Username: "reader-test01", Kind: AccountKind.Reader, Password: ReaderPassword),
            (Username: "admin-test001", Kind: AccountKind.Admin, Password: AdminPassword)
        };
        var configuration = new Dictionary<string, string?>();
        for (var index = 0; index < accounts.Length; index++)
        {
            var input = accounts[index];
            var account = new AccountDefinition { Username = input.Username, Kind = input.Kind };
            configuration[$"Authentication:Accounts:{index}:Username"] = input.Username;
            configuration[$"Authentication:Accounts:{index}:Kind"] = input.Kind.ToString();
            configuration[$"Authentication:Accounts:{index}:PasswordHash"] = passwords.Hash(account, input.Password);
        }
        return configuration;
    }

    /// <summary>
    /// Returns the matching ephemeral password or a deliberate invalid-password sentinel.
    /// </summary>
    /// <param name="kind">The cms, reader, or admin fixture selector, or an unknown-user negative case.</param>
    /// <returns>The matching ephemeral password, or an intentionally incorrect password for unknown selectors.</returns>
    public static string Password(string kind) => kind switch
    {
        "cms" => CmsPassword,
        "reader" => ReaderPassword,
        "admin" => AdminPassword,
        _ => "incorrect-password"
    };

    /// <summary>
    /// Returns the configured username or a deliberately unknown user for negative cases.
    /// </summary>
    /// <param name="kind">The cms, reader, or admin fixture selector, or an unknown-user negative case.</param>
    /// <returns>The matching fixture username, or a deliberately unknown username for negative cases.</returns>
    public static string Username(string kind) => kind switch
    {
        "cms" => "cms-test0001",
        "reader" => "reader-test01",
        "admin" => "admin-test001",
        _ => "unknown-user"
    };
}
