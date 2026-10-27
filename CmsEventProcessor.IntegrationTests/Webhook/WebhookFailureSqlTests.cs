using System.Diagnostics;
using CmsEventProcessor.Application.Processing.Contracts;
using CmsEventProcessor.IntegrationTests.Sql;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CmsEventProcessor.IntegrationTests.Webhook.WebhookScenarios;

namespace CmsEventProcessor.IntegrationTests.Webhook;

/// <summary>
/// Verifies HTTP behavior at real SQL commit, cancellation, deadline, locking, and unavailable-connection boundaries.
/// </summary>
/// <param name="fixture">The shared SQL instance providing isolated migrated catalogs.</param>
[Collection("SQL")]
[Trait("Category", "Sql")]
public sealed class WebhookFailureSqlTests(SqlServerFixture fixture)
{
    /// <summary>
    /// No HTTP success or committed-event log is observable while actual SQL commit acknowledgment is paused.
    /// </summary>
    /// <returns>A task completing after explicit commit release and immediate durable-state verification.</returns>
    [Fact]
    public async Task HttpSuccessWaitsForCommit()
    {
        await using var database = await fixture.CreateAsync();
        var probe = new CommitProbe { Pause = true };
        await using var factory = Factory(database, probe);
        using var client = Client(factory);
        var pending = PostAsync(client, key: "commit-key");
        try
        {
            await probe.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(pending.IsCompleted);
            Assert.DoesNotContain("Committed batch", string.Join('\n', factory.Logs.Messages));
        }
        finally { probe.Release.TrySetResult(); }
        using var response = await pending;
        Assert.Equal(200, (int)response.StatusCode);
        await using var observer = await database.WriterAsync();
        Assert.Single(await observer.Snapshots.ToListAsync());
        Assert.Single(await observer.Replays.ToListAsync());
    }

    /// <summary>
    /// A lost acknowledgment after actual commit returns a safe uncertain 503; a new HTTP host recovers the original durable result.
    /// </summary>
    /// <returns>A task completing after commit uncertainty, redaction, and exact replay metadata are verified.</returns>
    [Fact]
    public async Task LostAcknowledgmentCanBeRecoveredWithTheSameKeyAndBody()
    {
        await using var database = await fixture.CreateAsync();
        await using (var uncertain = Factory(database, new CommitProbe { LoseAcknowledgment = true }))
        {
            using var client = Client(uncertain);
            using var response = await PostAsync(client, key: "uncertain-key");
            await ProblemAsync(response, 503, uncertain, "commit_unknown");
            Assert.Equal(TimeSpan.FromSeconds(1), response.Headers.RetryAfter?.Delta);
        }
        await using var retry = Factory(database);
        using var retryClient = Client(retry);
        using var recovered = await PostAsync(retryClient, key: "uncertain-key");
        Assert.Equal(200, (int)recovered.StatusCode);
        await using var observer = await database.WriterAsync();
        Assert.Single(await observer.Snapshots.ToListAsync());
        var replay = Assert.Single(await observer.Replays.ToListAsync());
        Assert.Equal(replay.ResponseJson, await recovered.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// A cooperative processing deadline aborts a later write and returns 503 without committing the first event or key.
    /// </summary>
    /// <returns>A task completing after timeout status, total lifetime, and rollback are checked.</returns>
    [Fact]
    public async Task DeadlineReturnsSafe503AndRollsBack()
    {
        await using var database = await fixture.CreateAsync();
        var probe = new SaveProbe();
        await using var factory = Factory(database, probe);
        factory.Settings["Processing:Timeout"] = "00:00:02";
        factory.Settings["Processing:LockWait"] = "00:00:00.1";
        using var client = Client(factory);
        var elapsed = Stopwatch.StartNew();
        using var response = await PostAsync(client, ValidBody[..^1] + "," + ValidBody[1..].Replace("article-001", "second"), "timeout-key");
        await ProblemAsync(response, 503, factory, "processing_timeout");
        Assert.True(probe.Reached.Task.IsCompleted);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10));
        await using var observer = await database.WriterAsync();
        Assert.Empty(await observer.Snapshots.ToListAsync());
        Assert.Empty(await observer.Replays.ToListAsync());
    }

    /// <summary>
    /// Request abortion after the first flushed event propagates cancellation, rolls back, and permits a subsequent caller retry.
    /// </summary>
    /// <returns>A task completing after cancellation, durable-state absence, and successful retry are verified.</returns>
    [Fact]
    public async Task RequestAbortRollsBackAndReleasesMutationOwnership()
    {
        await using var database = await fixture.CreateAsync();
        var probe = new SaveProbe();
        await using var factory = Factory(database, probe);
        using var client = Client(factory);
        using var caller = new CancellationTokenSource();
        var body = ValidBody[..^1] + "," + ValidBody[1..].Replace("article-001", "second");
        var pending = PostAsync(client, body, "cancel-key", caller.Token);
        await probe.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        // The client may observe abortion before server cleanup; a coordinated retry waits for the previous transaction to end.
        using var retry = await PostAsync(client, body, "cancel-key");
        Assert.Equal(200, (int)retry.StatusCode);
        await using var observer = await database.WriterAsync();
        Assert.Equal(2, await observer.Snapshots.CountAsync());
        Assert.Single(await observer.Replays.ToListAsync());
        Assert.Contains("canceled by caller", string.Join('\n', factory.Logs.Messages));
    }

    /// <summary>
    /// An existing SQL mutation owner causes a bounded safe 503 and does not reserve the blocked request's key.
    /// </summary>
    /// <returns>A task completing after lock failure and a successful retry following owner disposal.</returns>
    [Fact]
    public async Task LockContentionReturns503ThenCallerCanRetry()
    {
        await using var database = await fixture.CreateAsync();
        await using var factory = Factory(database);
        factory.Settings["Processing:LockWait"] = "00:00:00.1";
        using var client = Client(factory);
        await using (var owner = await database.Services.GetRequiredService<IMutationTransactionFactory>().BeginAsync(default))
        {
            Assert.True(await owner.AcquireAsync(TimeSpan.FromSeconds(1), default));
            using var blocked = await PostAsync(client, key: "blocked-key");
            await ProblemAsync(blocked, 503, factory, "storage_unavailable");
        }
        using var retry = await PostAsync(client, key: "blocked-key");
        Assert.Equal(200, (int)retry.StatusCode);
    }

    /// <summary>
    /// An unreachable SQL endpoint returns a bounded sanitized 503 without leaking connection details, content, or credentials.
    /// </summary>
    /// <returns>A task completing after unavailable-storage response and log privacy are checked.</returns>
    [Fact]
    public async Task UnreachableDatabaseReturns503()
    {
        await using var database = await fixture.CreateAsync();
        await using var factory = Factory(database);
        var connection = new SqlConnectionStringBuilder(database.ConnectionString) { DataSource = "127.0.0.1,1", ConnectTimeout = 1 };
        factory.Settings["Persistence:WriteConnectionString"] = connection.ConnectionString;
        factory.Settings["Processing:Timeout"] = "00:00:03";
        factory.Settings["Processing:LockWait"] = "00:00:00.1";
        using var client = Client(factory);
        using var response = await PostAsync(client, key: "unavailable-key");
        await ProblemAsync(response, 503, factory, "storage_unavailable");
        Assert.DoesNotContain(connection.Password, string.Join('\n', factory.Logs.Messages));
    }
}
