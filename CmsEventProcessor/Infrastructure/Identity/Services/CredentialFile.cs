using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CmsEventProcessor.Application.Identity.Contracts;
using CmsEventProcessor.Application.Identity.Models;
using CmsEventProcessor.Infrastructure.Identity.Models;
using CmsEventProcessor.Infrastructure.Identity.Options;

namespace CmsEventProcessor.Infrastructure.Identity.Services;

/// <summary>
/// Provisions account files atomically while preserving existing identities and unrelated settings.
/// </summary>
internal sealed class CredentialFile : IAccountProvisioner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<AccountKind>() }
    };
    private readonly PasswordHashService passwords;

    /// <summary>
    /// Creates a file provisioner using the shared identity hashing implementation.
    /// </summary>
    /// <param name="passwords">The hashing service used for new and rotated account credentials.</param>
    public CredentialFile(PasswordHashService passwords)
    {
        this.passwords = passwords;
    }

    /// <summary>
    /// Creates CMS, reader, and administrator identities without overwriting an existing file.
    /// </summary>
    /// <param name="path">The new configuration file to create without overwriting an existing file.</param>
    /// <returns>The three safe identities and their one-time passwords, ordered CMS, reader, then administrator.</returns>
    /// <exception cref="System.ArgumentException">The configuration path is invalid.</exception>
    /// <exception cref="System.IO.IOException">The configuration cannot be read or atomically written.</exception>
    /// <exception cref="System.UnauthorizedAccessException">Filesystem permissions prevent the requested configuration operation.</exception>
    /// <exception cref="System.InvalidOperationException">The configuration file already exists and its accounts must be preserved.</exception>
    public IReadOnlyList<GeneratedCredential> Initialize(string path)
    {
        if (File.Exists(path))
        {
            throw new InvalidOperationException("The account file already exists. Existing credentials were preserved; use add or rotate explicitly.");
        }

        var credentials = new[] { Generate(AccountKind.Cms), Generate(AccountKind.Reader), Generate(AccountKind.Admin) };
        var settings = new AccountSettings { Accounts = credentials.Select(item => item.Definition).ToList() };
        var root = new JsonObject
        {
            [AccountSettings.SectionName] = new JsonObject
            {
                [nameof(AccountSettings.Accounts)] = JsonSerializer.SerializeToNode(settings.Accounts, JsonOptions)
            }
        };
        WriteAtomically(path, root, overwrite: false);
        return credentials.Select(item => item.Credential).ToArray();
    }

    /// <summary>
    /// Replaces one account's hash and returns its new password without changing other identities.
    /// </summary>
    /// <param name="path">The existing managed configuration file to update atomically.</param>
    /// <param name="username">The exact, case-sensitive username whose password should be replaced.</param>
    /// <returns>The unchanged safe identity and its replacement password, without exposing the stored hash.</returns>
    /// <exception cref="System.ArgumentException">The configuration path is invalid.</exception>
    /// <exception cref="System.IO.IOException">The configuration cannot be read or atomically written.</exception>
    /// <exception cref="System.UnauthorizedAccessException">Filesystem permissions prevent the requested configuration operation.</exception>
    /// <exception cref="System.Text.Json.JsonException">The configuration is not valid JSON or contains incompatible account values.</exception>
    /// <exception cref="System.InvalidOperationException">The account is absent or the existing configuration is invalid.</exception>
    public GeneratedCredential Rotate(string path, string username)
    {
        var (root, settings) = Read(path);
        var index = settings.Accounts.FindIndex(account => string.Equals(account.Username, username, StringComparison.Ordinal));
        if (index < 0)
        {
            throw new InvalidOperationException("The account was not found. No credentials were changed.");
        }

        var original = settings.Accounts[index];
        var password = Guid.NewGuid().ToString("D");
        var updated = original with { PasswordHash = passwords.Hash(original, password) };
        settings.Accounts[index] = updated;
        SaveAccounts(path, root, settings);
        return new GeneratedCredential(new AccountIdentity(updated.Username, updated.Kind), password);
    }

    /// <summary>
    /// Adds a uniquely named account of the requested kind while preserving all existing accounts.
    /// </summary>
    /// <param name="path">The existing managed configuration file whose unrelated settings must be retained.</param>
    /// <param name="kind">The supported CMS, reader, or administrator kind for the new identity.</param>
    /// <returns>The appended safe identity and its new password, without exposing the stored hash.</returns>
    /// <exception cref="System.ArgumentException">The configuration path is invalid.</exception>
    /// <exception cref="System.IO.IOException">The configuration cannot be read or atomically written.</exception>
    /// <exception cref="System.UnauthorizedAccessException">Filesystem permissions prevent the requested configuration operation.</exception>
    /// <exception cref="System.Text.Json.JsonException">The configuration is not valid JSON or contains incompatible account values.</exception>
    /// <exception cref="System.InvalidOperationException">The requested kind is unsupported or the existing configuration is invalid.</exception>
    public GeneratedCredential Add(string path, AccountKind kind)
    {
        if (kind is not (AccountKind.Cms or AccountKind.Reader or AccountKind.Admin))
        {
            throw new InvalidOperationException("Account kind must be Cms, Reader, or Admin.");
        }
        var (root, settings) = Read(path);
        (AccountDefinition Definition, GeneratedCredential Credential) credential;
        do
        {
            credential = Generate(kind);
        } while (settings.Accounts.Any(account => account.Username == credential.Definition.Username));
        settings.Accounts.Add(credential.Definition);
        SaveAccounts(path, root, settings);
        return credential.Credential;
    }

    /// <summary>
    /// Generates a bounded username, GUID password, and salted hash for a single identity.
    /// </summary>
    /// <param name="kind">The already-validated account kind used for the username prefix and permissions.</param>
    /// <returns>The private stored definition paired with the safe provisioning result for one-time disclosure.</returns>
    private (AccountDefinition Definition, GeneratedCredential Credential) Generate(AccountKind kind)
    {
        var username = $"{kind.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}";
        username = username[..Math.Min(username.Length, kind.ToString().Length + 13)];
        var account = new AccountDefinition { Username = username, Kind = kind };
        var password = Guid.NewGuid().ToString("D");
        return (account with { PasswordHash = passwords.Hash(account, password) },
            new GeneratedCredential(new AccountIdentity(account.Username, account.Kind), password));
    }

    /// <summary>
    /// Reads and validates managed account configuration before allowing an explicit mutation.
    /// </summary>
    /// <param name="path">The managed configuration file to parse and validate.</param>
    /// <returns>The original JSON object and validated authentication settings, retaining unrelated configuration fields.</returns>
    /// <exception cref="System.ArgumentException">The configuration path is invalid.</exception>
    /// <exception cref="System.IO.IOException">The configuration cannot be read or atomically written.</exception>
    /// <exception cref="System.UnauthorizedAccessException">Filesystem permissions prevent the requested configuration operation.</exception>
    /// <exception cref="System.Text.Json.JsonException">The configuration is not valid JSON or contains incompatible account values.</exception>
    /// <exception cref="System.InvalidOperationException">The configuration has no usable authentication section or fails account validation.</exception>
    private static (JsonObject Root, AccountSettings Settings) Read(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidOperationException("The account file must contain a JSON object.");
        var settings = root[AccountSettings.SectionName]?.Deserialize<AccountSettings>(JsonOptions)
            ?? throw new InvalidOperationException("The account file has no Authentication section.");
        var validation = new AccountSettingsValidator().Validate(null, settings);
        if (validation.Failed)
        {
            throw new InvalidOperationException("The account file is invalid. No credentials were changed.");
        }
        return (root, settings);
    }

    /// <summary>
    /// Replaces only the account array while retaining other JSON configuration properties.
    /// </summary>
    /// <param name="path">The existing file to replace atomically.</param>
    /// <param name="root">The parsed configuration object retaining unrelated settings.</param>
    /// <param name="settings">The validated account list to serialize into the authentication section.</param>
    /// <exception cref="System.ArgumentException">The configuration path is invalid.</exception>
    /// <exception cref="System.IO.IOException">The configuration cannot be read or atomically written.</exception>
    /// <exception cref="System.UnauthorizedAccessException">Filesystem permissions prevent the requested configuration operation.</exception>
    private static void SaveAccounts(string path, JsonObject root, AccountSettings settings)
    {
        root[AccountSettings.SectionName]![nameof(AccountSettings.Accounts)] =
            JsonSerializer.SerializeToNode(settings.Accounts, JsonOptions);
        WriteAtomically(path, root, overwrite: true);
    }

    /// <summary>
    /// Writes through a same-directory temporary file with owner-only Unix permissions before renaming.
    /// </summary>
    /// <param name="path">The destination configuration path, resolved before creating its temporary file.</param>
    /// <param name="root">The hash-only configuration document to serialize.</param>
    /// <param name="overwrite">Whether an existing destination may be replaced; initialization passes false.</param>
    /// <exception cref="System.ArgumentException">The configuration path is invalid.</exception>
    /// <exception cref="System.IO.IOException">The configuration cannot be read or atomically written.</exception>
    /// <exception cref="System.UnauthorizedAccessException">Filesystem permissions prevent the requested configuration operation.</exception>
    private static void WriteAtomically(string path, JsonObject root, bool overwrite)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            using (var stream = new FileStream(temporaryPath, options))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(root.ToJsonString(JsonOptions));
                writer.WriteLine();
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, fullPath, overwrite);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
