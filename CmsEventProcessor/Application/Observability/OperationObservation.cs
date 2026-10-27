using System.Diagnostics;

namespace CmsEventProcessor.Application.Observability;

/// <summary>
/// Records a single safe operation result and restores ambient tracing without exporting incoming request context.
/// </summary>
/// <param name="telemetry">The fixed instrument owner.</param>
/// <param name="operation">The validated fixed operation name.</param>
/// <param name="activity">The optional sanitized activity.</param>
/// <param name="previous">The prior ambient activity restored on completion.</param>
/// <param name="caller">The original caller token.</param>
internal sealed class OperationObservation(OperationTelemetry telemetry, string operation, Activity? activity, Activity? previous,
    CancellationToken caller) : IDisposable
{
    private readonly long started = Stopwatch.GetTimestamp();
    private string outcome = "Failed";
    private bool disposed;

    /// <summary>
    /// Selects a bounded outcome without logging errors, identifiers, or exception content.
    /// </summary>
    /// <param name="value">The known internal result name.</param>
    internal void SetOutcome(string value) => outcome = OperationTelemetry.SafeOutcome(value);

    /// <summary>
    /// Completes the observation once, preserving caller cancellation and the previous ambient activity.
    /// </summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (caller.IsCancellationRequested) outcome = "Canceled";
        activity?.SetTag("operation.outcome", outcome);
        if (outcome is not ("Succeeded" or "Replayed" or "NotFound" or "RolledBack")) activity?.SetStatus(ActivityStatusCode.Error);
        if (operation != "http") telemetry.RecordOperation(operation, outcome, Stopwatch.GetElapsedTime(started).TotalSeconds);
        activity?.Dispose();
        Activity.Current = previous;
    }
}
