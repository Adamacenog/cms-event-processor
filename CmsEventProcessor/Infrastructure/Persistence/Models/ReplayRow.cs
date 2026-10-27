namespace CmsEventProcessor.Infrastructure.Persistence.Models;

/// <summary>
/// Stores authenticated response metadata and digests without submitted bodies, payload copies, or plaintext keys.
/// </summary>
internal sealed class ReplayRow
{
    public string Principal { get; set; } = string.Empty;
    public byte[] KeyHash { get; set; } = [];
    public byte[] BodyHash { get; set; } = [];
    public string ResponseJson { get; set; } = string.Empty;
    public DateTimeOffset CompletedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
