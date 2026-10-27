using CmsEventProcessor.Domain.Entities.Models;

namespace CmsEventProcessor.Application.Processing.Models;

/// <summary>
/// Groups the current live or deleted state of one source identity without exposing EF tracking objects.
/// </summary>
/// <param name="snapshot">The retained entity snapshot, or null for absent/deleted state.</param>
/// <param name="deletion">The ordering marker, or null for a live or unknown entity.</param>
internal sealed class StoredEntityState(EntitySnapshot? snapshot, DeletionMarker? deletion)
{
    public EntitySnapshot? Snapshot { get; } = snapshot;
    public DeletionMarker? Deletion { get; } = deletion;
}
