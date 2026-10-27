namespace CmsEventProcessor.Application.Processing.Models;

/// <summary>
/// Represents successful, scoped replay metadata without retaining a request body, plaintext key, or copied payload.
/// </summary>
/// <param name="principal">The exact authenticated CMS principal used to scope this result.</param>
/// <param name="keyHash">The SHA-256 digest of the opaque idempotency key.</param>
/// <param name="bodyHash">The SHA-256 digest of the exact request bytes.</param>
/// <param name="response">The original successful response metadata.</param>
/// <param name="completedAt">The completion instant used for retention.</param>
/// <param name="expiresAt">The instant after which the result no longer reserves its key.</param>
internal sealed class ReplayRecord(string principal, ReadOnlyMemory<byte> keyHash, ReadOnlyMemory<byte> bodyHash,
    BatchSuccess response, DateTimeOffset completedAt, DateTimeOffset expiresAt)
{
    public string Principal { get; } = principal;
    public ReadOnlyMemory<byte> KeyHash { get; } = keyHash;
    public ReadOnlyMemory<byte> BodyHash { get; } = bodyHash;
    public BatchSuccess Response { get; } = response;
    public DateTimeOffset CompletedAt { get; } = completedAt;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
}
