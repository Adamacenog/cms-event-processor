using System.Security.Cryptography;
using System.Text;
using CmsEventProcessor.Domain.Entities.Models;

namespace CmsEventProcessor.Application.Processing.Models;

/// <summary>
/// Owns validated event inputs and request digests without retaining submitted batch bytes or the plaintext key.
/// </summary>
internal sealed class BatchRequest
{
    /// <summary>
    /// Copies bounded event references and hashes exact request bytes before they can be changed by the caller.
    /// </summary>
    /// <param name="principal">The authenticated CMS identity, scoped exactly and never logged.</param>
    /// <param name="events">One to one hundred fully validated domain events in submitted order.</param>
    /// <param name="body">The exact wire bytes used for keyed replay comparison; no copy of them is retained.</param>
    /// <param name="idempotencyKey">An optional opaque key, hashed without trimming or case normalization.</param>
    /// <exception cref="ArgumentException">The identity, event collection, body, or supplied key is invalid.</exception>
    public BatchRequest(string principal, IReadOnlyList<CmsEvent> events, ReadOnlySpan<byte> body, string? idempotencyKey = null)
    {
        if (string.IsNullOrWhiteSpace(principal) || principal.Length > 128 || principal.Any(char.IsControl) ||
            principal != principal.Trim())
        {
            throw new ArgumentException("A bounded CMS principal is required.", nameof(principal));
        }
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count is < 1 or > 100 || events.Any(source => source is null))
        {
            throw new ArgumentException("A batch must contain 1-100 validated events.", nameof(events));
        }
        if (body.IsEmpty)
        {
            throw new ArgumentException("Exact request bytes are required.", nameof(body));
        }
        if (idempotencyKey is not null && (string.IsNullOrWhiteSpace(idempotencyKey) ||
            idempotencyKey.Length > 128 || idempotencyKey.Any(char.IsControl)))
        {
            throw new ArgumentException("The idempotency key must be bounded and nonblank.", nameof(idempotencyKey));
        }
        Principal = principal;
        Events = Array.AsReadOnly(events.ToArray());
        BodyHash = SHA256.HashData(body);
        KeyHash = idempotencyKey is null ? ReadOnlyMemory<byte>.Empty :
            SHA256.HashData(new UTF8Encoding(false, true).GetBytes(idempotencyKey));
    }

    public string Principal { get; }
    public IReadOnlyList<CmsEvent> Events { get; }
    public ReadOnlyMemory<byte> BodyHash { get; }
    public ReadOnlyMemory<byte> KeyHash { get; }
}
