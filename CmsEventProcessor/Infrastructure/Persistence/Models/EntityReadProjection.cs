namespace CmsEventProcessor.Infrastructure.Persistence.Models;

/// <summary>
/// Holds only the columns needed for a consumer snapshot, excluding rowversion and all deletion/replay metadata.
/// </summary>
/// <param name="id">The source identifier.</param>
/// <param name="payload">The stored JSON object, parsed only after bounded SQL materialization.</param>
/// <param name="version">The current CMS revision.</param>
/// <param name="timestamp">The source watermark.</param>
/// <param name="published">The CMS publication flag.</param>
/// <param name="disabled">The local administrator override.</param>
internal sealed class EntityReadProjection(string id, string payload, long version, DateTimeOffset timestamp, bool published, bool disabled)
{
    public string Id { get; } = id;
    public string Payload { get; } = payload;
    public long Version { get; } = version;
    public DateTimeOffset Timestamp { get; } = timestamp;
    public bool Published { get; } = published;
    public bool Disabled { get; } = disabled;
}
