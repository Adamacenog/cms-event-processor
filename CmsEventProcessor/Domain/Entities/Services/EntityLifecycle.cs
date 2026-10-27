using CmsEventProcessor.Domain.Entities.Models;

namespace CmsEventProcessor.Domain.Entities.Services;

/// <summary>
/// Evaluates the accepted version/time decision table without mutating inputs, accessing storage, or reading a clock.
/// </summary>
internal static class EntityLifecycle
{
    /// <summary>
    /// Evaluates one source event against either a live snapshot, a deletion marker, or an unknown identity.
    /// </summary>
    /// <param name="source">The validated incoming source event.</param>
    /// <param name="current">The current snapshot for exactly this identifier, or null.</param>
    /// <param name="deletion">The deletion marker for exactly this identifier, or null; it cannot coexist with a snapshot.</param>
    /// <returns>The proposed replacement state and an applied, skipped, or conflict outcome; unchanged decisions preserve input state.</returns>
    /// <exception cref="ArgumentNullException">The incoming event is null.</exception>
    /// <exception cref="ArgumentException">State is contradictory or belongs to a different ordinal identifier.</exception>
    public static EntityTransition Apply(CmsEvent source, EntitySnapshot? current = null, DeletionMarker? deletion = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (current is not null && deletion is not null ||
            current is not null && !string.Equals(source.Id, current.Id, StringComparison.Ordinal) ||
            deletion is not null && !string.Equals(source.Id, deletion.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException("Lifecycle state must belong to the event identifier and contain either a snapshot or a deletion marker.");
        }

        if (deletion is not null)
        {
            if (source.Timestamp <= deletion.Timestamp)
            {
                var reason = source.Type == CmsEventType.Delete && source.Timestamp == deletion.Timestamp
                    ? TransitionReason.Duplicate : TransitionReason.Stale;
                return new EntityTransition(TransitionOutcome.Skipped, reason, null, deletion);
            }
        }
        else if (current is not null && source.Timestamp < current.CmsTimestamp)
        {
            return new EntityTransition(TransitionOutcome.Skipped, TransitionReason.Stale, current, null);
        }

        if (source.Type == CmsEventType.Delete)
        {
            return new EntityTransition(TransitionOutcome.Applied, TransitionReason.Deleted,
                null, new DeletionMarker(source.Id, source.Timestamp));
        }

        if (current is not null)
        {
            if (source.Version < current.Version)
            {
                return new EntityTransition(TransitionOutcome.Skipped, TransitionReason.Stale, current, null);
            }
            if (source.Version == current.Version)
            {
                if (!current.Payload.HasSameContent(source.Payload))
                {
                    return new EntityTransition(TransitionOutcome.Conflict, TransitionReason.VersionPayloadConflict, current, null);
                }
                if (source.Timestamp == current.CmsTimestamp)
                {
                    var requestedPublished = source.Type == CmsEventType.Publish;
                    if (requestedPublished == current.CmsPublished)
                    {
                        return new EntityTransition(TransitionOutcome.Skipped, TransitionReason.Duplicate, current, null);
                    }
                    if (requestedPublished)
                    {
                        return new EntityTransition(TransitionOutcome.Skipped, TransitionReason.Stale, current, null);
                    }
                }
            }
        }

        var published = source.Type == CmsEventType.Publish;
        var snapshot = new EntitySnapshot(source.Id, source.Version!.Value, source.Payload!, source.Timestamp,
            published, current?.AdminDisabled ?? false);
        return new EntityTransition(TransitionOutcome.Applied,
            published ? TransitionReason.Published : TransitionReason.Unpublished, snapshot, null);
    }
}
