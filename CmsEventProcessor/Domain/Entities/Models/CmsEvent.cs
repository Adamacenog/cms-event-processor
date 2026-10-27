namespace CmsEventProcessor.Domain.Entities.Models;

/// <summary>
/// Represents one validated source action with normalized time and no HTTP or persistence dependencies.
/// </summary>
internal sealed class CmsEvent
{
    /// <summary>
    /// Checks action-specific invariants while retaining opaque identifiers and unmodified content.
    /// </summary>
    /// <param name="type">The recognized publish, unpublish, or delete action.</param>
    /// <param name="id">The opaque, case-sensitive entity identifier.</param>
    /// <param name="timestamp">The source event time, normalized to UTC without consulting the local clock.</param>
    /// <param name="version">The required positive publish/unpublish revision, or an optional positive delete revision.</param>
    /// <param name="payload">The required publish/unpublish object, or an optional validated delete object.</param>
    /// <exception cref="ArgumentNullException">The identifier is null or publish/unpublish content is missing.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The action is unsupported or a supplied version is nonpositive.</exception>
    /// <exception cref="ArgumentException">The identifier is invalid or publish/unpublish has no version.</exception>
    public CmsEvent(CmsEventType type, string id, DateTimeOffset timestamp, long? version = null, EntityPayload? payload = null)
    {
        EntityRules.ValidateId(id);
        if (type is not (CmsEventType.Publish or CmsEventType.Unpublish or CmsEventType.Delete))
        {
            throw new ArgumentOutOfRangeException(nameof(type), "The source action is unsupported.");
        }
        if (version.HasValue)
        {
            EntityRules.ValidateVersion(version.Value);
        }
        if (type != CmsEventType.Delete)
        {
            if (!version.HasValue)
            {
                throw new ArgumentException("Publish and unpublish events require a positive version.", nameof(version));
            }
            ArgumentNullException.ThrowIfNull(payload);
        }

        Type = type;
        Id = id;
        Timestamp = timestamp.ToUniversalTime();
        Version = version;
        Payload = payload;
    }

    public CmsEventType Type { get; }
    public string Id { get; }
    public DateTimeOffset Timestamp { get; }
    public long? Version { get; }
    public EntityPayload? Payload { get; }
}
