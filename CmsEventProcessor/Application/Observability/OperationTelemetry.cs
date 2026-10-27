using System.Diagnostics;
using System.Diagnostics.Metrics;
using CmsEventProcessor.Application.Processing.Models;
using Microsoft.Extensions.Logging;

namespace CmsEventProcessor.Application.Observability;

/// <summary>
/// Emits only fixed operation/outcome labels and numeric measurements, without source data, credentials, SQL, or incoming baggage.
/// </summary>
/// <param name="logger">The optional numeric debug-log sink used by the reproducible load exercise.</param>
internal sealed class OperationTelemetry(ILogger<OperationTelemetry> logger)
{
    public const string SourceName = "CmsEventProcessor.Operations";
    private static readonly Meter Meter = new(SourceName, "1.0.0");
    private static readonly ActivitySource Activities = new(SourceName, "1.0.0");
    private static readonly Counter<long> Completed = Meter.CreateCounter<long>("cms.operations.completed", "{operation}");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>("cms.operations.failures", "{operation}");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("cms.operations.duration", "s");
    private static readonly Counter<long> Events = Meter.CreateCounter<long>("cms.events.committed", "{event}");
    private static readonly Histogram<double> Transactions = Meter.CreateHistogram<double>("cms.transactions.duration", "s");
    private static readonly Histogram<double> LockWait = Meter.CreateHistogram<double>("cms.mutation.lock_wait", "s");
    private static readonly Counter<long> Contention = Meter.CreateCounter<long>("cms.mutation.lock_contention", "{attempt}");
    private static readonly Counter<long> LockFailures = Meter.CreateCounter<long>("cms.mutation.lock_failures", "{attempt}");
    private static readonly Counter<long> Requests = Meter.CreateCounter<long>("cms.http.requests", "{request}");
    private static readonly Histogram<double> HttpDuration = Meter.CreateHistogram<double>("cms.http.duration", "s");

    /// <summary>
    /// Begins a fixed-name operation, starting a fresh local trace when the parent is not our own sanitized instrumentation.
    /// </summary>
    /// <param name="operation">The fixed http, batch, disable, read, or transaction operation.</param>
    /// <param name="caller">The caller token used to classify cancellation accurately.</param>
    /// <returns>A scope recording completion once and restoring the original activity when disposed.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The operation is not a supported fixed name.</exception>
    internal OperationObservation Begin(string operation, CancellationToken caller)
    {
        if (operation is not ("http" or "batch" or "disable" or "read" or "transaction")) throw new ArgumentOutOfRangeException(nameof(operation));
        var previous = Activity.Current;
        if (previous?.Source.Name != SourceName) Activity.Current = null;
        var activity = Activities.StartActivity(operation, operation == "http" ? ActivityKind.Server : ActivityKind.Internal);
        return new OperationObservation(this, operation, activity, previous, caller);
    }

    /// <summary>
    /// Records one completed operation using fixed names/outcomes only.
    /// </summary>
    /// <param name="operation">The validated fixed operation.</param>
    /// <param name="outcome">The validated bounded result.</param>
    /// <param name="seconds">The measured complete operation duration.</param>
    internal void RecordOperation(string operation, string outcome, double seconds)
    {
        var tags = new TagList { { "operation", operation }, { "outcome", outcome } };
        Completed.Add(1, tags);
        Duration.Record(seconds, tags);
        if (outcome is not ("Succeeded" or "Replayed" or "NotFound" or "RolledBack")) Failures.Add(1, tags);
    }

    /// <summary>
    /// Counts committed event effects once; replay and rolled-back events do not inflate effect throughput.
    /// </summary>
    /// <param name="applied">The number of newly committed applied decisions.</param>
    /// <param name="skipped">The number of newly committed skipped decisions.</param>
    internal void RecordEvents(int applied, int skipped)
    {
        Events.Add(applied, new KeyValuePair<string, object?>("decision", "applied"));
        Events.Add(skipped, new KeyValuePair<string, object?>("decision", "skipped"));
    }

    /// <summary>
    /// Measures transaction lifetime through disposal and SQL-reported lock contention without guessing commit acknowledgment.
    /// </summary>
    /// <param name="seconds">The lifetime after SQL began the transaction, including cleanup.</param>
    /// <param name="lockSeconds">The actual application-lock acquisition duration.</param>
    /// <param name="lockCode">The SQL lock return code, or null if execution was interrupted.</param>
    /// <param name="attempted">Whether commit was attempted.</param>
    /// <param name="committed">Whether commit was acknowledged.</param>
    /// <param name="lockAttempted">Whether an acquisition command was actually attempted.</param>
    internal void RecordTransaction(double seconds, double lockSeconds, int? lockCode, bool attempted, bool committed, bool lockAttempted)
    {
        var outcome = committed ? "committed" : attempted ? "commit_unknown" : "rolled_back";
        Transactions.Record(seconds, new KeyValuePair<string, object?>("outcome", outcome));
        if (lockAttempted)
        {
            LockWait.Record(lockSeconds, new KeyValuePair<string, object?>("acquired", lockCode >= 0));
            if (lockCode == 1) Contention.Add(1);
            if (lockCode is null or < 0) LockFailures.Add(1);
        }
        logger.LogDebug(new EventId(7001, "TransactionMeasurement"),
            "Transaction measurement: duration {DurationMs} ms; lock wait {LockWaitMs} ms; contended {Contended}; outcome {Outcome}; completed {CompletedAtUtc}.",
            seconds * 1000, lockSeconds * 1000, lockCode == 1, outcome, DateTimeOffset.UtcNow.ToString("O"));
    }

    /// <summary>
    /// Records HTTP metrics from whitelisted route templates, normalized methods, and bounded status values.
    /// </summary>
    /// <param name="route">The whitelisted template or unmatched marker.</param>
    /// <param name="method">GET, POST, PUT, or OTHER.</param>
    /// <param name="status">A valid HTTP status or the cancellation marker 499.</param>
    /// <param name="seconds">The full wrapped pipeline duration.</param>
    internal void RecordHttp(string route, string method, int status, double seconds)
    {
        var tags = new TagList { { "http.route", route }, { "http.request.method", method }, { "http.response.status_code", status } };
        Requests.Add(1, tags);
        HttpDuration.Record(seconds, tags);
    }

    /// <summary>
    /// Restricts operation outcomes to known values rather than accepting arbitrary strings as metric labels.
    /// </summary>
    /// <param name="value">The internal operation result.</param>
    /// <returns>The validated result or a fixed Failed fallback.</returns>
    internal static string SafeOutcome(string value) => value is "Failed" or "Canceled" or "Replayed" or "RolledBack" ||
        Enum.TryParse<MutationStatus>(value, out var parsed) && Enum.IsDefined(parsed) && value == parsed.ToString() ? value : "Failed";
}
