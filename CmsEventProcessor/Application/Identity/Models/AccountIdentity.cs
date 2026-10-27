namespace CmsEventProcessor.Application.Identity.Models;

/// <summary>
/// Represents a trusted account identity shared by authentication and explicit provisioning results.
/// </summary>
/// <param name="Username">The case-sensitive username from a verified or explicitly provisioned account.</param>
/// <param name="Kind">The server-assigned permissions and trust boundary.</param>
public sealed record AccountIdentity(string Username, AccountKind Kind);
