namespace CmsEventProcessor.Domain.Entities.Models;

/// <summary>
/// Explains lifecycle decisions without including confidential content or transport-specific status codes.
/// </summary>
internal enum TransitionReason
{
    Published,
    Unpublished,
    Deleted,
    Stale,
    Duplicate,
    VersionPayloadConflict
}
