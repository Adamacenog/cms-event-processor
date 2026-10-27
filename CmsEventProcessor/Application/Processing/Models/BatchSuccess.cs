namespace CmsEventProcessor.Application.Processing.Models;

/// <summary>
/// Carries the exact successful response metadata that becomes durable alongside entity changes.
/// </summary>
/// <param name="batchId">The stable correlation identifier reused by keyed retries.</param>
/// <param name="outcomes">The committed per-event metadata, copied to prevent caller mutation.</param>
internal sealed class BatchSuccess(Guid batchId, IReadOnlyList<EventOutcome> outcomes)
{
    public Guid BatchId { get; } = batchId;
    public IReadOnlyList<EventOutcome> Outcomes { get; } = Array.AsReadOnly(outcomes.ToArray());
}
