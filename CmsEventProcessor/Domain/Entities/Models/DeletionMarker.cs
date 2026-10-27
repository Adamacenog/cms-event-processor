namespace CmsEventProcessor.Domain.Entities.Models;

/// <summary>
/// Retains only identity and source deletion time to prevent stale content from recreating a deleted lifecycle.
/// </summary>
internal sealed class DeletionMarker
{
    /// <summary>
    /// Creates content-free ordering metadata with a normalized source timestamp.
    /// </summary>
    /// <param name="id">The deleted opaque, case-sensitive identifier.</param>
    /// <param name="timestamp">The source deletion time, normalized to UTC.</param>
    /// <exception cref="ArgumentNullException">The identifier is null.</exception>
    /// <exception cref="ArgumentException">The identifier violates its bounded, opaque format.</exception>
    public DeletionMarker(string id, DateTimeOffset timestamp)
    {
        EntityRules.ValidateId(id);
        Id = id;
        Timestamp = timestamp.ToUniversalTime();
    }

    public string Id { get; }
    public DateTimeOffset Timestamp { get; }
}
