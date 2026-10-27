namespace CmsEventProcessor.Application.Processing.Models;

/// <summary>
/// Retains only final event metadata, never submitted entity content, for a successful response and keyed replay.
/// </summary>
/// <param name="index">The zero-based position within the validated batch.</param>
/// <param name="id">The source identifier returned only to the authenticated caller, never logged.</param>
/// <param name="outcome">The applied or skipped classification.</param>
/// <param name="reasonCode">The safe publication, deletion, stale, or duplicate explanation.</param>
internal sealed class EventOutcome(int index, string id, string outcome, string reasonCode)
{
    public int Index { get; } = index;
    public string Id { get; } = id;
    public string Outcome { get; } = outcome;
    public string ReasonCode { get; } = reasonCode;
}
