using CmsEventProcessor.IntegrationTests.Entities;
using CmsEventProcessor.IntegrationTests.Sql;
using Microsoft.EntityFrameworkCore;
using static CmsEventProcessor.IntegrationTests.Webhook.WebhookScenarios;

namespace CmsEventProcessor.IntegrationTests.Operations;

/// <summary>
/// Verifies custom transaction/lock/effect instrumentation at actual commit, conflict, replay, and acknowledgment boundaries.
/// </summary>
/// <param name="fixture">The shared SQL instance with per-case owned catalogs.</param>
[Collection("SQL")]
[Trait("Category", "Sql")]
public sealed class OperationMetricsSqlTests(SqlServerFixture fixture)
{
    /// <summary>
    /// Acknowledged work emits transaction/effect metrics; replay reports its bounded outcome without repeating effects, and conflict remains a failure.
    /// </summary>
    /// <returns>A task completing after real commit/replay/conflict and fixed instrument tags are verified.</returns>
    [Fact]
    public async Task TelemetryTracksAcknowledgedWorkReplayAndFailure()
    {
        await using var database = await fixture.CreateAsync();
        using var capture = new TelemetryCapture();
        await using var factory = ConsumerScenarios.Factory(database);
        using var cms = Client(factory);
        using (var first = await PostAsync(cms, key: "metrics-key")) Assert.Equal(200, (int)first.StatusCode);
        var eventMeasurements = capture.Measurements.Count(value => value.StartsWith("cms.events.committed", StringComparison.Ordinal));
        using (var replay = await PostAsync(cms, key: "metrics-key")) Assert.Equal(200, (int)replay.StatusCode);
        Assert.Equal(eventMeasurements, capture.Measurements.Count(value => value.StartsWith("cms.events.committed", StringComparison.Ordinal)));
        using (var conflict = await PostAsync(cms, ValidBody + " ", "metrics-key")) await ProblemAsync(conflict, 409, factory);
        Assert.Contains(capture.Measurements, value => value.StartsWith("cms.transactions.duration", StringComparison.Ordinal));
        Assert.Contains(capture.Measurements, value => value.Contains("outcome=Replayed", StringComparison.Ordinal));
        Assert.Contains(capture.Measurements, value => value.StartsWith("cms.operations.failures", StringComparison.Ordinal));
        Assert.DoesNotContain("article-001", string.Join('\n', capture.Measurements));
        Assert.DoesNotContain("metrics-key", string.Join('\n', capture.Measurements));
    }

    /// <summary>
    /// A fault after actual commit records conservative commit_unknown telemetry while durable state remains replayable.
    /// </summary>
    /// <returns>A task completing after acknowledgment uncertainty, real state, and safe transaction metrics are checked.</returns>
    [Fact]
    public async Task LostAcknowledgmentIsNeverReportedAsConfirmedCommitInTelemetry()
    {
        await using var database = await fixture.CreateAsync();
        using var capture = new TelemetryCapture();
        await using var factory = ConsumerScenarios.Factory(database, writeProbe: new CommitProbe { LoseAcknowledgment = true });
        using var cms = Client(factory);
        using var response = await PostAsync(cms, key: "metrics-uncertain");
        await ProblemAsync(response, 503, factory, "commit_unknown");
        Assert.Contains(capture.Measurements, value => value.StartsWith("cms.transactions.duration", StringComparison.Ordinal) && value.Contains("commit_unknown", StringComparison.Ordinal));
        Assert.DoesNotContain(capture.Measurements, value => value.StartsWith("cms.events.committed", StringComparison.Ordinal));
        await using var observer = await database.WriterAsync();
        Assert.Single(await observer.Snapshots.ToListAsync());
    }
}
