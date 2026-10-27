namespace CmsEventProcessor.Application.Processing.Models;

/// <summary>
/// Classifies successful mutations and expected failures without coupling application code to HTTP status codes.
/// </summary>
internal enum MutationStatus
{
    Succeeded,
    NotFound,
    VersionConflict,
    IdempotencyConflict,
    StorageUnavailable,
    TimedOut,
    CommitUnknown
}
