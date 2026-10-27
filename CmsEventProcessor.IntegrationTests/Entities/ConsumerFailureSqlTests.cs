using System.Text.Json;
using CmsEventProcessor.IntegrationTests.Sql;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using static CmsEventProcessor.IntegrationTests.Webhook.WebhookScenarios;

namespace CmsEventProcessor.IntegrationTests.Entities;

/// <summary>
/// Verifies consumer read failures and administrator transactions under real SQL contention, commit uncertainty, and request cancellation.
/// </summary>
/// <param name="fixture">The shared SQL instance with isolated test catalogs.</param>
[Collection("SQL")]
[Trait("Category", "Sql")]
public sealed class ConsumerFailureSqlTests(SqlServerFixture fixture)
{
    /// <summary>
    /// A racing administrator disable and CMS publish preserve both the latest content and local override regardless of lock acquisition order.
    /// </summary>
    /// <param name="disableFirst">Whether the administrator holds the first paused transaction.</param>
    /// <returns>A task completing after both HTTP operations commit and both consumer views are checked.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentIngestionAndDisablePreserveOverride(bool disableFirst)
    {
        await using var database = await fixture.CreateAsync();
        await database.Processor.ProcessAsync(SqlScenarios.Request([SqlScenarios.Source()]));
        var probe = new CommitProbe { Pause = true };
        await using var factory = ConsumerScenarios.Factory(database, writeProbe: probe);
        using var cms = Client(factory);
        using var admin = Client(factory, "admin");
        using var reader = Client(factory, "reader");
        var first = disableFirst ? admin.PutAsync("/api/entities/article-001/disable", null) :
            PostAsync(cms, System.Text.Encoding.UTF8.GetString(SqlScenarios.Body([SqlScenarios.Source(version: 2, seconds: 1)])));
        await probe.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var second = disableFirst ? PostAsync(cms, System.Text.Encoding.UTF8.GetString(SqlScenarios.Body([SqlScenarios.Source(version: 2, seconds: 1)]))) :
            admin.PutAsync("/api/entities/article-001/disable", null);
        probe.Release.TrySetResult();
        using var firstResponse = await first;
        using var secondResponse = await second;
        Assert.Equal(disableFirst ? 204 : 200, (int)firstResponse.StatusCode);
        Assert.Equal(disableFirst ? 200 : 204, (int)secondResponse.StatusCode);
        using var current = await admin.GetAsync("/api/entities/article-001");
        using var body = JsonDocument.Parse(await current.Content.ReadAsStringAsync());
        Assert.Equal(2, body.RootElement.GetProperty("version").GetInt64());
        Assert.True(body.RootElement.GetProperty("adminDisabled").GetBoolean());
        using var hidden = await reader.GetAsync("/api/entities/article-001");
        await ProblemAsync(hidden, 404, factory);
    }

    /// <summary>
    /// Disabling waits for real commit; a lost acknowledgment produces safe uncertainty and a repeat operation safely confirms the override.
    /// </summary>
    /// <param name="loseAcknowledgment">Whether the test drops acknowledgment after SQL actually commits.</param>
    /// <returns>A task completing after the HTTP commit boundary and durable idempotent retry are checked.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisableWaitsForCommitAndCanBeRetriedAfterUncertainty(bool loseAcknowledgment)
    {
        await using var database = await fixture.CreateAsync();
        await database.Processor.ProcessAsync(SqlScenarios.Request([SqlScenarios.Source()]));
        var probe = new CommitProbe { Pause = !loseAcknowledgment, LoseAcknowledgment = loseAcknowledgment };
        await using (var factory = ConsumerScenarios.Factory(database, writeProbe: probe))
        {
            using var admin = Client(factory, "admin");
            var pending = admin.PutAsync("/api/entities/article-001/disable", null);
            await probe.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (!loseAcknowledgment) Assert.False(pending.IsCompleted);
            probe.Release.TrySetResult();
            using var result = await pending;
            if (loseAcknowledgment) await ProblemAsync(result, 503, factory, "commit_unknown");
            else Assert.Equal(204, (int)result.StatusCode);
        }
        await using var restarted = ConsumerScenarios.Factory(database);
        using var retryAdmin = Client(restarted, "admin");
        using var retry = await retryAdmin.PutAsync("/api/entities/article-001/disable", null);
        Assert.Equal(204, (int)retry.StatusCode);
        await using var observer = await database.WriterAsync();
        Assert.True((await observer.Snapshots.SingleAsync()).AdminDisabled);
    }

    /// <summary>
    /// Consumer reads use their independently configured connection and return safe 503s even while the writer remains healthy.
    /// </summary>
    /// <param name="route">The listing or lookup operation.</param>
    /// <returns>A task completing after healthy ingestion, unavailable reads, and connection-secret redaction are checked.</returns>
    [Theory]
    [InlineData("/api/entities")]
    [InlineData("/api/entities/article-001")]
    public async Task IndependentReadConnectionFailuresAreSafe(string route)
    {
        await using var database = await fixture.CreateAsync();
        await using var factory = ConsumerScenarios.Factory(database);
        var bad = new SqlConnectionStringBuilder(database.ConnectionString) { DataSource = "127.0.0.1,1", ConnectTimeout = 1 };
        factory.Settings["Persistence:ReadConnectionString"] = bad.ConnectionString;
        factory.Settings["Processing:Timeout"] = "00:00:03";
        factory.Settings["Processing:LockWait"] = "00:00:00.1";
        using var cms = Client(factory);
        using var reader = Client(factory, "reader");
        await ConsumerScenarios.SendAsync(cms);
        using var response = await reader.GetAsync(route);
        await ProblemAsync(response, 503, factory, "storage_unavailable");
        Assert.DoesNotContain(bad.Password, string.Join('\n', factory.Logs.Messages));
    }

    /// <summary>
    /// A paused real-context read observes the total cooperative timeout and returns a safe read_timeout 503 without retrying.
    /// </summary>
    /// <returns>A task completing after deadline propagation and a single attempted query are checked.</returns>
    [Fact]
    public async Task ReadDeadlineIsMappedWithoutAutomaticRetry()
    {
        await using var database = await fixture.CreateAsync();
        var probe = new ReadProbe { Pause = true };
        await using var factory = ConsumerScenarios.Factory(database, readProbe: probe);
        factory.Settings["Processing:Timeout"] = "00:00:02";
        factory.Settings["Processing:LockWait"] = "00:00:00.1";
        using var reader = Client(factory, "reader");
        using var response = await reader.GetAsync("/api/entities");
        await ProblemAsync(response, 503, factory, "read_timeout");
        Assert.True(probe.Reached.Task.IsCompleted);
        Assert.Single(probe.Commands);
    }

    /// <summary>
    /// HTTP abortion reaches the active read command token and a fresh host can read normally afterward.
    /// </summary>
    /// <returns>A task completing after caller cancellation and independent subsequent read success.</returns>
    [Fact]
    public async Task CallerAbortCancelsTheRead()
    {
        await using var database = await fixture.CreateAsync();
        var probe = new ReadProbe { Pause = true };
        await using (var factory = ConsumerScenarios.Factory(database, readProbe: probe))
        {
            using var reader = Client(factory, "reader");
            using var caller = new CancellationTokenSource();
            var pending = reader.GetAsync("/api/entities", caller.Token);
            await probe.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        await using var fresh = ConsumerScenarios.Factory(database);
        using var normal = Client(fresh, "reader");
        using var result = await normal.GetAsync("/api/entities");
        Assert.Equal(200, (int)result.StatusCode);
    }
}
