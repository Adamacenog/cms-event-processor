namespace CmsEventProcessor.Infrastructure.Persistence.Models;

/// <summary>
/// Stores only a deleted identity and its ordering watermark, with no copied payload or administrator override.
/// </summary>
internal sealed class DeletionRow
{
    public string Id { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
}
