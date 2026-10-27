using CmsEventProcessor.Application.Identity.Models;

namespace CmsEventProcessor.Infrastructure.Identity.Models;

/// <summary>
/// Defines one configured identity without retaining its plaintext password.
/// </summary>
internal sealed record AccountDefinition
{
    /// <summary>
    /// Compared ordinally and case-sensitively; identities remain distinct across authentication schemes.
    /// </summary>
    public string Username { get; init; } = string.Empty;
    /// <summary>
    /// Only a bounded Identity V3 hash is accepted; plaintext passwords must never be assigned.
    /// </summary>
    public string PasswordHash { get; init; } = string.Empty;
    /// <summary>
    /// Assigned by server configuration; administrator access never implies CMS ingestion permission.
    /// </summary>
    public AccountKind Kind { get; init; }
}
