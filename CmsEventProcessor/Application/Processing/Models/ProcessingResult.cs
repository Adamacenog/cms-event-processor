namespace CmsEventProcessor.Application.Processing.Models;

/// <summary>
/// Separates durable success from safe conflict, timeout, storage, and ambiguous-commit responses.
/// </summary>
/// <param name="status">The result classification for the future HTTP adapter.</param>
/// <param name="success">Committed response metadata, present only for success or a successful replay.</param>
/// <param name="eventIndex">The failing source position for a version/content conflict.</param>
/// <param name="isReplay">Whether success came from an unexpired stored response rather than new domain evaluation.</param>
internal sealed class ProcessingResult(MutationStatus status, BatchSuccess? success = null, int? eventIndex = null, bool isReplay = false)
{
    public MutationStatus Status { get; } = status;
    public BatchSuccess? Success { get; } = success;
    public int? EventIndex { get; } = eventIndex;
    public bool IsReplay { get; } = isReplay;
}
