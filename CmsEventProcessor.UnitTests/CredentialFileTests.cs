using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CmsEventProcessor.Api.Provisioning;
using CmsEventProcessor.Application.Identity.Models;
using CmsEventProcessor.Infrastructure.Identity.Models;
using CmsEventProcessor.Infrastructure.Identity.Options;
using CmsEventProcessor.Infrastructure.Identity.Services;

namespace CmsEventProcessor.UnitTests;

/// <summary>
/// Verifies provisioning, explicit account changes, secret handling, and non-destructive file updates.
/// </summary>
public sealed class CredentialFileTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"cms-credentials-tests-{Guid.NewGuid():N}");
    private string ConfigurationPath => Path.Combine(directory, "appsettings.Local.json");
    private readonly CredentialFile files = new(new PasswordHashService());
    private readonly PasswordHashService passwords = new();

    /// <summary>
    /// First-time setup creates all three account kinds with unique compliant usernames, GUID passwords,
    /// valid salted hashes, no persisted plaintext, and owner-only file permissions on Unix.
    /// </summary>
    [Fact]
    public void InitializeCreatesThreeDistinctAccountsAndPersistsOnlyHashes()
    {
        var generated = files.Initialize(ConfigurationPath);
        var contents = File.ReadAllText(ConfigurationPath);
        var definitions = ReadSettings().Accounts;
        Assert.Equal(new[] { AccountKind.Cms, AccountKind.Reader, AccountKind.Admin }, generated.Select(item => item.Account.Kind));
        Assert.Equal(3, generated.Select(item => item.Account.Username).Distinct().Count());
        foreach (var credential in generated)
        {
            var definition = definitions.Single(account => account.Username == credential.Account.Username);
            Assert.InRange(credential.Account.Username.Length, 10, 20);
            Assert.True(Guid.TryParseExact(credential.Password, "D", out _));
            Assert.DoesNotContain(credential.Password, contents);
            Assert.True(passwords.Verify(definition, credential.Password));
            Assert.False(passwords.Verify(definition, "wrong-password"));
            Assert.True(PasswordHashService.IsSupportedHash(definition.PasswordHash));
        }
        Assert.True(new AccountSettingsValidator().Validate(null, ReadSettings()).Succeeded);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(ConfigurationPath));
        }
    }

    /// <summary>
    /// Repeated initialization fails without altering any bytes of an existing credential file.
    /// </summary>
    [Fact]
    public void RepeatingSetupPreservesExistingFileExactly()
    {
        files.Initialize(ConfigurationPath);
        var original = File.ReadAllBytes(ConfigurationPath);
        Assert.Throws<InvalidOperationException>(() => files.Initialize(ConfigurationPath));
        Assert.Equal(original, File.ReadAllBytes(ConfigurationPath));
    }

    /// <summary>
    /// Rotating the reader replaces its password/hash, invalidates the old password, preserves other accounts
    /// and unrelated JSON settings, and leaves no temporary or plaintext-password files behind.
    /// </summary>
    [Fact]
    public void RotationReplacesOnlySelectedHashAndPreservesOtherConfiguration()
    {
        var initial = files.Initialize(ConfigurationPath);
        var originalDefinitions = ReadSettings().Accounts.ToArray();
        var root = JsonNode.Parse(File.ReadAllText(ConfigurationPath))!;
        root["OtherSetting"] = "preserve-me";
        File.WriteAllText(ConfigurationPath, root.ToJsonString());
        var updated = files.Rotate(ConfigurationPath, initial[1].Account.Username);
        var stored = ReadSettings().Accounts;
        Assert.Equal(originalDefinitions[0], stored[0]);
        Assert.Equal(originalDefinitions[2], stored[2]);
        Assert.Equal(initial[1].Account.Username, updated.Account.Username);
        Assert.Equal(AccountKind.Reader, updated.Account.Kind);
        Assert.NotEqual(initial[1].Password, updated.Password);
        Assert.False(passwords.Verify(stored[1], initial[1].Password));
        Assert.True(passwords.Verify(stored[1], updated.Password));
        Assert.DoesNotContain(updated.Password, File.ReadAllText(ConfigurationPath));
        Assert.Equal("preserve-me", JsonNode.Parse(File.ReadAllText(ConfigurationPath))!["OtherSetting"]!.GetValue<string>());
        Assert.Single(Directory.GetFiles(directory));
    }

    /// <summary>
    /// Adding a CMS, reader, or administrator identity retains the original accounts and verifies the new password.
    /// </summary>
    /// <param name="kind">The supported account kind to append to existing configuration.</param>
    [Theory]
    [InlineData("Cms")]
    [InlineData("Reader")]
    [InlineData("Admin")]
    public void AddingAnAccountPreservesExistingAccounts(string kind)
    {
        files.Initialize(ConfigurationPath);
        var originalDefinitions = ReadSettings().Accounts.ToArray();
        var added = files.Add(ConfigurationPath, Enum.Parse<AccountKind>(kind));
        var stored = ReadSettings().Accounts;
        Assert.Equal(4, stored.Count);
        Assert.Equal(originalDefinitions, stored.Take(3));
        Assert.Equal(added.Account.Username, stored[3].Username);
        Assert.Equal(added.Account.Kind, stored[3].Kind);
        Assert.True(passwords.Verify(stored[3], added.Password));
    }

    /// <summary>
    /// Unknown-user rotation and unsupported account kinds fail without changing the credential file.
    /// </summary>
    [Fact]
    public void UnknownRotationAndInvalidKindDoNotChangeTheFile()
    {
        files.Initialize(ConfigurationPath);
        var original = File.ReadAllBytes(ConfigurationPath);
        Assert.Throws<InvalidOperationException>(() => files.Rotate(ConfigurationPath, "unknown-user"));
        Assert.Throws<InvalidOperationException>(() => files.Add(ConfigurationPath, AccountKind.Unknown));
        Assert.Equal(original, File.ReadAllBytes(ConfigurationPath));
    }

    /// <summary>
    /// Malformed configuration remains untouched and its contents are absent from command output and errors.
    /// </summary>
    [Fact]
    public void MalformedConfigurationIsNotOverwritten()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(ConfigurationPath, "{bad json}");
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, CredentialCommand.Run(["rotate", "--file", ConfigurationPath, "--username", "reader-user"], files, output, error));
        Assert.Equal("{bad json}", File.ReadAllText(ConfigurationPath));
        Assert.Empty(output.ToString());
        Assert.DoesNotContain("{bad json}", error.ToString());
    }

    /// <summary>
    /// Successful initialization prints three new passwords once; repeating it prints none and preserves credentials.
    /// </summary>
    [Fact]
    public void CommandDisplaysNewPasswordsOnceAndDoesNotRedisplayOnRepeat()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var arguments = new[] { "init", "--file", ConfigurationPath };
        Assert.Equal(0, CredentialCommand.Run(arguments, files, output, error));
        var passwordLines = output.ToString().Split('\n').Where(line => line.StartsWith("Password: ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, passwordLines.Length);
        Assert.All(passwordLines, line => Assert.True(Guid.TryParse(line[10..].Trim(), out _)));
        Assert.Empty(error.ToString());
        output.GetStringBuilder().Clear();
        Assert.Equal(1, CredentialCommand.Run(arguments, files, output, error));
        Assert.Empty(output.ToString());
    }

    /// <summary>
    /// Unknown commands, plaintext-password options, and duplicate file arguments fail with usage and no files.
    /// </summary>
    /// <param name="args">The invalid command/option sequence presented to the provisioning entry point.</param>
    [Theory]
    [InlineData("unknown")]
    [InlineData("init", "--password", "never-accept-password-arguments")]
    [InlineData("init", "--file", "one", "--file", "two")]
    public void InvalidCommandsProduceUsageWithoutCreatingFiles(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, CredentialCommand.Run(args, files, output, error));
        Assert.Empty(output.ToString());
        Assert.Contains("accounts init", error.ToString());
        Assert.False(Directory.Exists(directory));
    }

    /// <summary>
    /// Deserializes only the test-owned authentication section using the provisioning enum format.
    /// </summary>
    /// <returns>The authentication settings deserialized from this test instance's managed file.</returns>
    /// <exception cref="System.IO.IOException">The test-owned configuration file cannot be read.</exception>
    /// <exception cref="System.Text.Json.JsonException">The test-owned configuration is malformed or incompatible with its DTO.</exception>
    private AccountSettings ReadSettings() =>
        JsonNode.Parse(File.ReadAllText(ConfigurationPath))![AccountSettings.SectionName]!
            .Deserialize<AccountSettings>(new JsonSerializerOptions { Converters = { new JsonStringEnumConverter<AccountKind>() } })!;

    /// <summary>
    /// Deletes the files and directory created exclusively for this test instance.
    /// </summary>
    /// <exception cref="System.IO.IOException">A test-owned file or directory cannot be removed during cleanup.</exception>
    /// <exception cref="System.UnauthorizedAccessException">Filesystem permissions prevent test-directory cleanup.</exception>
    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.GetFiles(directory))
            {
                File.Delete(file);
            }
            Directory.Delete(directory);
        }
    }
}
