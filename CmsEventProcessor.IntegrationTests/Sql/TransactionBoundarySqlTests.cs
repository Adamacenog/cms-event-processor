using System.Diagnostics;
using CmsEventProcessor.Application.Processing.Contracts;
using CmsEventProcessor.Application.Processing.Models;
using CmsEventProcessor.Application.Processing.Options;
using CmsEventProcessor.Application.Processing.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CmsEventProcessor.IntegrationTests.Sql.SqlScenarios;

namespace CmsEventProcessor.IntegrationTests.Sql;

/// <summary>
/// Exercises real SQL transactions at commit, lock, cancellation, deadline, and interrupted-connection boundaries.
/// </summary>
/// <param name="fixture">The shared pinned SQL instance with isolated test catalogs.</param>
[Collection("SQL")]
[Trait("Category", "Sql")]
public sealed class TransactionBoundarySqlTests(SqlServerFixture fixture)
{
    /// <summary>
    /// The processor cannot return or log committed outcomes while the real commit is deliberately paused.
    /// </summary>
    /// <returns>A task completing after the paused boundary and acknowledged durable state are checked.</returns>
    [Fact]
    public async Task SuccessWaitsForCommitAcknowledgment()
    {
        await using var database = await fixture.CreateAsync();
        var probe = new CommitProbe { Pause = true };
        await using var instance = database.CreateServices(interceptors: [probe]);
        var processing = instance.GetRequiredService<BatchProcessor>().ProcessAsync(Request([Source()], "commit-key"));
        await probe.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(processing.IsCompleted);
        Assert.DoesNotContain("Committed batch", string.Join("\n", database.Logs.Messages));
        probe.Release.TrySetResult();
        Assert.Equal(MutationStatus.Succeeded, (await processing).Status);
        await using var observer = await database.WriterAsync();
        Assert.Single(await observer.Snapshots.ToListAsync());
        Assert.Single(await observer.Replays.ToListAsync());
    }

    /// <summary>
    /// Caller cancellation after a flushed first event rolls back all state/key effects and releases mutation ownership.
    /// </summary>
    /// <returns>A task completing after cancellation, rollback, and a successful subsequent caller retry.</returns>
    [Fact]
    public async Task CallerCancellationRollsBackEarlierWritesAndReleasesTheLock()
    {
        await using var database = await fixture.CreateAsync();
        var probe = new SaveProbe();
        await using var instance = database.CreateServices(interceptors: [probe]);
        using var caller = new CancellationTokenSource();
        var request = Request([Source("first"), Source("second")], "cancel-key");
        var processing = instance.GetRequiredService<BatchProcessor>().ProcessAsync(request, caller.Token);
        await probe.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
        await using (var observer = await database.WriterAsync())
        {
            Assert.Empty(await observer.Snapshots.ToListAsync());
            Assert.Empty(await observer.Replays.ToListAsync());
        }
        Assert.Equal(MutationStatus.Succeeded, (await database.Processor.ProcessAsync(request)).Status);
    }

    /// <summary>
    /// One processing deadline cancels a paused later write, rolls back prior state, and does not restart work automatically.
    /// </summary>
    /// <returns>A task completing after the bounded timeout, rollback, and lock release are verified.</returns>
    [Fact]
    public async Task DeadlineBoundsTheWholeAttemptAndRollsBack()
    {
        await using var database = await fixture.CreateAsync();
        var probe = new SaveProbe();
        var options = new ProcessingOptions { Timeout = TimeSpan.FromSeconds(3), LockWait = TimeSpan.FromMilliseconds(100) };
        await using var instance = database.CreateServices(options, interceptors: [probe]);
        var elapsed = Stopwatch.StartNew();
        var processing = instance.GetRequiredService<BatchProcessor>().ProcessAsync(Request([Source("first"), Source("second")], "timeout-key"));
        await probe.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(MutationStatus.TimedOut, (await processing).Status);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(8));
        await using (var observer = await database.WriterAsync())
        {
            Assert.Empty(await observer.Snapshots.ToListAsync());
            Assert.Empty(await observer.Replays.ToListAsync());
        }
        Assert.Equal(MutationStatus.Succeeded, (await database.Processor.ProcessAsync(Request([Source("after-timeout")]))).Status);
    }

    /// <summary>
    /// An incompatible transaction-owned lock returns an expected unavailable result without accessing batch state.
    /// </summary>
    /// <returns>A task completing after SQL lock failure and successful processing once the holder rolls back.</returns>
    [Fact]
    public async Task FailedLockAcquisitionNeverProcessesTheBatch()
    {
        await using var database = await fixture.CreateAsync();
        var factory = database.Services.GetRequiredService<IMutationTransactionFactory>();
        await using (var holder = await factory.BeginAsync(default))
        {
            Assert.True(await holder.AcquireAsync(TimeSpan.FromSeconds(1), default));
            var options = new ProcessingOptions { Timeout = TimeSpan.FromSeconds(3), LockWait = TimeSpan.FromMilliseconds(100) };
            await using var contender = database.CreateServices(options);
            var result = await contender.GetRequiredService<BatchProcessor>().ProcessAsync(Request([Source()], "locked-key"));
            Assert.Equal(MutationStatus.StorageUnavailable, result.Status);
            Assert.Null(result.Success);
            await using var observer = await database.WriterAsync();
            Assert.Empty(await observer.Snapshots.ToListAsync());
            Assert.Empty(await observer.Replays.ToListAsync());
        }
        Assert.Equal(MutationStatus.Succeeded, (await database.Processor.ProcessAsync(Request([Source()], "locked-key"))).Status);
    }

    /// <summary>
    /// A caller can cancel a request contending for the global mutation resource without leaving a new reservation.
    /// </summary>
    /// <returns>A task completing after the canceled attempt and subsequent successful retry.</returns>
    [Fact]
    public async Task CallerCancellationInterruptsLockContention()
    {
        await using var database = await fixture.CreateAsync();
        var factory = database.Services.GetRequiredService<IMutationTransactionFactory>();
        await using (var holder = await factory.BeginAsync(default))
        {
            Assert.True(await holder.AcquireAsync(TimeSpan.FromSeconds(1), default));
            using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                database.Processor.ProcessAsync(Request([Source()], "wait-key"), caller.Token));
        }
        Assert.Equal(MutationStatus.Succeeded, (await database.Processor.ProcessAsync(Request([Source()], "wait-key"))).Status);
    }

    /// <summary>
    /// Ingestion and administrator disabling coordinate across independent service instances without losing the override.
    /// </summary>
    /// <returns>A task completing after the racing source update and local disable are both durably represented.</returns>
    [Fact]
    public async Task ConcurrentPublishAndDisablePreserveBothMutations()
    {
        await using var database = await fixture.CreateAsync();
        await database.Processor.ProcessAsync(Request([Source()]));
        var probe = new CommitProbe { Pause = true };
        await using var publisher = database.CreateServices(interceptors: [probe]);
        var publishing = publisher.GetRequiredService<BatchProcessor>()
            .ProcessAsync(Request([Source(version: 2, seconds: 1)]));
        await probe.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var disabling = database.Disabler.DisableAsync("article-001");
        probe.Release.TrySetResult();
        Assert.Equal(MutationStatus.Succeeded, (await publishing).Status);
        Assert.Equal(MutationStatus.Succeeded, await disabling);
        await using var observer = await database.WriterAsync();
        var snapshot = await observer.Snapshots.SingleAsync();
        Assert.Equal(2, snapshot.Version);
        Assert.True(snapshot.CmsPublished);
        Assert.True(snapshot.AdminDisabled);
        Assert.Equal(MutationStatus.Succeeded, await database.Disabler.DisableAsync("article-001"));
    }

    /// <summary>
    /// Racing different bodies under one scoped key produce one committed result and one key conflict.
    /// </summary>
    /// <returns>A task completing after only the winning request's state and metadata are found.</returns>
    [Fact]
    public async Task ConcurrentDifferentBodiesReserveOnlyOneKey()
    {
        await using var database = await fixture.CreateAsync();
        var results = await Task.WhenAll(
            database.Processor.ProcessAsync(Request([Source("first")], "one-key")),
            database.Processor.ProcessAsync(Request([Source("second")], "one-key")));
        Assert.Single(results, result => result.Status == MutationStatus.Succeeded);
        Assert.Single(results, result => result.Status == MutationStatus.IdempotencyConflict);
        await using var observer = await database.WriterAsync();
        Assert.Single(await observer.Snapshots.ToListAsync());
        Assert.Single(await observer.Replays.ToListAsync());
    }

    /// <summary>
    /// A deliberately lost postcommit acknowledgment is reported as unknown, while a retry recovers the durable success.
    /// </summary>
    /// <returns>A task completing after actual committed SQL state, safe logging, and original keyed replay are verified.</returns>
    [Fact]
    public async Task LostCommitAcknowledgmentIsUnknownAndSafelyReplayable()
    {
        await using var database = await fixture.CreateAsync();
        var probe = new CommitProbe { LoseAcknowledgment = true };
        await using var interrupted = database.CreateServices(interceptors: [probe]);
        var request = Request([Source()], "ambiguous-key");
        var result = await interrupted.GetRequiredService<BatchProcessor>().ProcessAsync(request);
        Assert.Equal(MutationStatus.CommitUnknown, result.Status);
        Assert.Null(result.Success);
        await using (var observer = await database.WriterAsync())
        {
            Assert.Single(await observer.Snapshots.ToListAsync());
            Assert.Single(await observer.Replays.ToListAsync());
        }
        var messages = string.Join("\n", database.Logs.Messages);
        Assert.DoesNotContain("CONFIDENTIAL_COMMIT_SENTINEL", messages);
        Assert.DoesNotContain("Committed batch", messages);
        var replay = await database.Processor.ProcessAsync(request);
        Assert.Equal(MutationStatus.Succeeded, replay.Status);
        Assert.True(replay.IsReplay);
        Assert.Equal("applied", replay.Success!.Outcomes[0].Outcome);
    }

    /// <summary>
    /// Killing the real SQL session after an earlier batch write leaves no partial state and permits a caller retry.
    /// </summary>
    /// <returns>A task completing after connection interruption, server rollback, and recovery are verified.</returns>
    [Fact]
    public async Task InterruptedSqlSessionRollsBackEarlierWrites()
    {
        await using var database = await fixture.CreateAsync();
        var probe = new SaveProbe();
        await using var interrupted = database.CreateServices(interceptors: [probe]);
        var request = Request([Source("first"), Source("second")], "disconnect-key");
        var processing = interrupted.GetRequiredService<BatchProcessor>().ProcessAsync(request);
        await probe.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(probe.SessionId > 0);
        await using (var killer = new SqlConnection(database.ConnectionString))
        {
            await killer.OpenAsync();
            await using var command = killer.CreateCommand();
            command.CommandText = "DECLARE @statement nvarchar(100) = N'KILL ' + CONVERT(nvarchar(10), @session); EXEC sys.sp_executesql @statement;";
            command.Parameters.Add("@session", System.Data.SqlDbType.Int).Value = probe.SessionId;
            await command.ExecuteNonQueryAsync();
        }
        probe.Release.TrySetResult();
        Assert.Equal(MutationStatus.StorageUnavailable, (await processing).Status);
        await using (var observer = await database.WriterAsync())
        {
            Assert.Empty(await observer.Snapshots.ToListAsync());
            Assert.Empty(await observer.Replays.ToListAsync());
        }
        Assert.Equal(MutationStatus.Succeeded, (await database.Processor.ProcessAsync(request)).Status);
    }

    /// <summary>
    /// An unreachable database returns a bounded unavailable result without retrying; a later healthy caller attempt succeeds.
    /// </summary>
    /// <returns>A task completing after unavailable connection handling and independent recovery are checked.</returns>
    [Fact]
    public async Task DatabaseUnavailabilityIsBoundedAndDoesNotReserveWork()
    {
        await using var database = await fixture.CreateAsync();
        var unreachable = new SqlConnectionStringBuilder(database.ConnectionString)
        {
            DataSource = "127.0.0.1,1",
            ConnectTimeout = 1
        };
        var options = new ProcessingOptions { Timeout = TimeSpan.FromSeconds(3), LockWait = TimeSpan.FromMilliseconds(100) };
        await using var unavailable = database.CreateServices(options, unreachable.ConnectionString);
        var elapsed = Stopwatch.StartNew();
        var request = Request([Source()], "unavailable-key");
        Assert.Equal(MutationStatus.StorageUnavailable,
            (await unavailable.GetRequiredService<BatchProcessor>().ProcessAsync(request)).Status);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(6));
        Assert.Equal(MutationStatus.Succeeded, (await database.Processor.ProcessAsync(request)).Status);
    }
}
