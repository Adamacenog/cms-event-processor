using System.Buffers.Binary;
using CmsEventProcessor.Application.Identity.Models;
using CmsEventProcessor.Infrastructure.Identity.Models;
using CmsEventProcessor.Infrastructure.Identity.Options;
using CmsEventProcessor.Infrastructure.Identity.Services;
using Microsoft.Extensions.Options;

namespace CmsEventProcessor.UnitTests;

/// <summary>
/// Verifies startup configuration rejection and the catalog's snapshot and scheme boundaries.
/// </summary>
public sealed class AccountSettingsTests
{
    private static readonly PasswordHashService Passwords = new();
    private static readonly AccountDefinition Reader = Create("reader-test01", AccountKind.Reader);
    private static readonly AccountDefinition Cms = Create("cms-test0001", AccountKind.Cms);
    private static readonly AccountDefinition Admin = Create("admin-test001", AccountKind.Admin);

    /// <summary>
    /// One CMS, reader, and administrator with valid distinct identities form accepted startup configuration.
    /// </summary>
    [Fact]
    public void CompleteConfigurationIsValid() => Assert.True(Validate([Cms, Reader, Admin]));

    /// <summary>
    /// Empty, short, long, colon-containing, and newline-terminated usernames are rejected before startup.
    /// </summary>
    /// <param name="username">The invalid replacement for an otherwise valid reader username.</param>
    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("username-is-too-long-for-configuration")]
    [InlineData("reader:test01")]
    [InlineData("reader-test01\n")]
    public void InvalidUsernameIsRejected(string username) =>
        Assert.False(Validate([Cms, Reader with { Username = username }, Admin]));

    /// <summary>
    /// Missing required identities, duplicate usernames, and unknown account-kind values all invalidate configuration.
    /// </summary>
    [Fact]
    public void DuplicateAndMissingKindsAreRejected()
    {
        Assert.False(Validate([]));
        Assert.False(Validate([Cms, Reader]));
        Assert.False(Validate([Cms, Reader, Admin with { Username = Reader.Username }]));
        Assert.False(Validate([Cms, Reader, Admin with { Kind = (AccountKind)99 }]));
    }

    /// <summary>
    /// Empty, plaintext, and truncated hash inputs fail validation without echoing supplied hash text.
    /// </summary>
    /// <param name="hash">The unsupported value presented as a configured password hash.</param>
    [Theory]
    [InlineData("")]
    [InlineData("plaintext-must-not-be-accepted")]
    [InlineData("AQAAAAIAAYagAAAAEA==")]
    public void InvalidHashIsRejectedWithoutEchoingIt(string hash)
    {
        var result = new AccountSettingsValidator().Validate(null,
            new AccountSettings { Accounts = [Cms, Reader with { PasswordHash = hash }, Admin] });
        Assert.True(result.Failed);
        if (hash.Length > 0)
        {
            Assert.DoesNotContain(hash, result.FailureMessage);
        }
    }

    /// <summary>
    /// A syntactically valid hash with an excessive iteration count is rejected to bound authentication work.
    /// </summary>
    [Fact]
    public void HashWithUnboundedWorkFactorIsRejected()
    {
        var hash = Convert.FromBase64String(Reader.PasswordHash);
        BinaryPrimitives.WriteUInt32BigEndian(hash.AsSpan(5, 4), uint.MaxValue);
        Assert.False(PasswordHashService.IsSupportedHash(Convert.ToBase64String(hash)));
    }

    /// <summary>
    /// The catalog retains its startup accounts after the source list changes, accepts the correct reader password,
    /// and rejects cross-scheme access, case-changed usernames, and incorrect passwords.
    /// </summary>
    [Fact]
    public void CatalogKeepsItsStartupSnapshotAndSeparatesSchemes()
    {
        var account = new AccountDefinition { Username = "reader-test02", Kind = AccountKind.Reader };
        var originalPassword = Guid.NewGuid().ToString("D");
        account = account with { PasswordHash = Passwords.Hash(account, originalPassword) };
        var options = new AccountSettings { Accounts = [Cms, account, Admin] };
        var catalog = new AccountCatalog(Options.Create(options), Passwords);
        options.Accounts.Clear();
        Assert.Equal(new AccountIdentity(account.Username, account.Kind),
            catalog.Authenticate(AccountScope.Consumer, account.Username, originalPassword));
        Assert.Null(catalog.Authenticate(AccountScope.Cms, account.Username, originalPassword));
        Assert.Null(catalog.Authenticate(AccountScope.Unknown, account.Username, originalPassword));
        Assert.Null(catalog.Authenticate((AccountScope)99, account.Username, originalPassword));
        Assert.Null(catalog.Authenticate(AccountScope.Consumer, account.Username.ToUpperInvariant(), originalPassword));
        Assert.Null(catalog.Authenticate(AccountScope.Consumer, account.Username, "wrong-password"));
    }

    /// <summary>
    /// Runs the production validator against a specific account combination under test.
    /// </summary>
    /// <param name="accounts">The account combination whose validation result is under test.</param>
    /// <returns>True when the production validator accepts the supplied account combination; otherwise false.</returns>
    private static bool Validate(List<AccountDefinition> accounts) =>
        new AccountSettingsValidator().Validate(null, new AccountSettings { Accounts = accounts }).Succeeded;

    /// <summary>
    /// Creates a valid hashed fixture identity so negative cases change only their intended setting.
    /// </summary>
    /// <param name="username">The fixture username to preserve in the generated definition.</param>
    /// <param name="kind">The server-side kind assigned to the fixture identity.</param>
    /// <returns>An account definition containing a new salted password hash for controlled validation tests.</returns>
    private static AccountDefinition Create(string username, AccountKind kind)
    {
        var account = new AccountDefinition { Username = username, Kind = kind };
        return account with { PasswordHash = Passwords.Hash(account, Guid.NewGuid().ToString("D")) };
    }
}
