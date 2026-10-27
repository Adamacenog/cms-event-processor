namespace CmsEventProcessor.Domain.Entities.Models;

/// <summary>
/// Carries a pure lifecycle decision and proposed replacement state without claiming persistence or transaction success.
/// </summary>
internal sealed class EntityTransition
{
    /// <summary>
    /// Packages an evaluated decision without retaining the input event or discarded content.
    /// </summary>
    /// <param name="outcome">Whether the source action applies, is skipped, or conflicts.</param>
    /// <param name="reason">The safe explanation for the decision.</param>
    /// <param name="snapshot">The retained snapshot, or null after deletion and for an absent entity.</param>
    /// <param name="deletion">The retained deletion marker, or null for a live lifecycle.</param>
    internal EntityTransition(TransitionOutcome outcome, TransitionReason reason, EntitySnapshot? snapshot, DeletionMarker? deletion)
    {
        Outcome = outcome;
        Reason = reason;
        Snapshot = snapshot;
        Deletion = deletion;
    }

    public TransitionOutcome Outcome { get; }
    public TransitionReason Reason { get; }
    public EntitySnapshot? Snapshot { get; }
    public DeletionMarker? Deletion { get; }
}
