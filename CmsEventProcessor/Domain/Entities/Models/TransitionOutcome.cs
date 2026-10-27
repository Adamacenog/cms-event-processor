namespace CmsEventProcessor.Domain.Entities.Models;

/// <summary>
/// Distinguishes a proposed state change, an ignored delivery, and a same-version content conflict.
/// </summary>
internal enum TransitionOutcome
{
    Applied,
    Skipped,
    Conflict
}
