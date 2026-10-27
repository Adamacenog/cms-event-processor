using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using CmsEventProcessor.Application.Observability;

namespace CmsEventProcessor.IntegrationTests.Operations;

/// <summary>
/// Observes only the custom source/meter so tests can inspect every emitted tag without enabling automatic request instrumentation.
/// </summary>
internal sealed class TelemetryCapture : IDisposable
{
    private readonly MeterListener metrics = new();
    private readonly ActivityListener traces = new();
    public ConcurrentQueue<Activity> Activities { get; } = new();
    public ConcurrentQueue<string> Measurements { get; } = new();

    /// <summary>
    /// Enables custom measurements and recorded activities with no filtering of their emitted tag values.
    /// </summary>
    public TelemetryCapture()
    {
        metrics.InstrumentPublished = (instrument, listener) =>
        { if (instrument.Meter.Name == OperationTelemetry.SourceName) listener.EnableMeasurementEvents(instrument); };
        metrics.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) => Capture(instrument.Name, tags));
        metrics.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) => Capture(instrument.Name, tags));
        metrics.Start();
        traces.ShouldListenTo = source => source.Name == OperationTelemetry.SourceName;
        traces.Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded;
        traces.ActivityStopped = Activities.Enqueue;
        ActivitySource.AddActivityListener(traces);
    }

    /// <summary>
    /// Copies only instrument/tag text for exhaustive privacy assertions.
    /// </summary>
    /// <param name="name">The emitted instrument name.</param>
    /// <param name="tags">Every emitted measurement tag.</param>
    private void Capture(string name, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var text = name;
        foreach (var tag in tags) text += $"|{tag.Key}={tag.Value}";
        Measurements.Enqueue(text);
    }

    /// <summary>
    /// Detaches listeners without changing the production source or meter lifetime.
    /// </summary>
    public void Dispose() { metrics.Dispose(); traces.Dispose(); }
}
