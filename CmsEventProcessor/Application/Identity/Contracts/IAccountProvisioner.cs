using CmsEventProcessor.Application.Identity.Models;

namespace CmsEventProcessor.Application.Identity.Contracts;

/// <summary>
/// Provides explicit account initialization, addition, and rotation independently of command-line presentation.
/// </summary>
public interface IAccountProvisioner
{
    /// <summary>
    /// Initializes the three required identities without replacing an existing account configuration.
    /// </summary>
    /// <param name="path">The new account configuration location.</param>
    /// <returns>The CMS, reader, and administrator identities with newly generated passwords.</returns>
    /// <exception cref="System.InvalidOperationException">The configuration already exists.</exception>
    /// <exception cref="System.ArgumentException">The configuration location is invalid.</exception>
    /// <exception cref="System.IO.IOException">The configuration cannot be created safely.</exception>
    /// <exception cref="System.UnauthorizedAccessException">Access to the configuration location is denied.</exception>
    IReadOnlyList<GeneratedCredential> Initialize(string path);

    /// <summary>
    /// Adds one identity while preserving existing accounts and unrelated configuration.
    /// </summary>
    /// <param name="path">The existing account configuration location.</param>
    /// <param name="kind">The supported kind assigned by the operator.</param>
    /// <returns>The added identity and its newly generated password.</returns>
    /// <exception cref="System.InvalidOperationException">The kind or existing account configuration is invalid.</exception>
    /// <exception cref="System.Text.Json.JsonException">Existing account configuration cannot be parsed into account definitions.</exception>
    /// <exception cref="System.ArgumentException">The configuration location is invalid.</exception>
    /// <exception cref="System.IO.IOException">The configuration cannot be read or updated safely.</exception>
    /// <exception cref="System.UnauthorizedAccessException">Access to the configuration location is denied.</exception>
    GeneratedCredential Add(string path, AccountKind kind);

    /// <summary>
    /// Replaces one account's password without changing its identity or permissions.
    /// </summary>
    /// <param name="path">The existing account configuration location.</param>
    /// <param name="username">The exact username selected for explicit rotation.</param>
    /// <returns>The selected identity and its replacement password.</returns>
    /// <exception cref="System.InvalidOperationException">The account is absent or its configuration is invalid.</exception>
    /// <exception cref="System.Text.Json.JsonException">Existing account configuration cannot be parsed into account definitions.</exception>
    /// <exception cref="System.ArgumentException">The configuration location is invalid.</exception>
    /// <exception cref="System.IO.IOException">The configuration cannot be read or updated safely.</exception>
    /// <exception cref="System.UnauthorizedAccessException">Access to the configuration location is denied.</exception>
    GeneratedCredential Rotate(string path, string username);
}
