using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CmsEventProcessor.Application.Processing.Models;
using CmsEventProcessor.Application.Processing.Services;
using CmsEventProcessor.Domain.Entities.Models;
using CmsEventProcessor.Infrastructure.Persistence.Contexts;
using CmsEventProcessor.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CmsEventProcessor.IntegrationTests.Sql.SqlScenarios;

namespace CmsEventProcessor.IntegrationTests.Sql;

/// <summary>
/// Verifies migrations, atomic state/replay persistence, ordering, retention, and read/write behavior against real SQL Server.
/// </summary>
/// <param name="fixture">The shared pinned container or explicitly configured test server.</param>
[Collection("SQL")]
[Trait("Category", "Sql")]
public sealed class PersistenceSqlTests(SqlServerFixture fixture)
{
    /// <summary>
    /// Checked-in migrations apply idempotently and the read context observes committed state without tracking it.
    /// </summary>
    /// <returns>A task completing after migration history and no-tracking reads are verified.</returns>
    [Fact]
    public async Task MigrationsApplyAndReadContextUsesNoTracking()
    {
        await using var database = await fixture.CreateAsync();
        await using var writer = await database.WriterAsync();
        await writer.Database.MigrateAsync();
        Assert.Single(await writer.Database.GetAppliedMigrationsAsync());
        Assert.False(writer.Database.HasPendingModelChanges());
        Assert.Equal(MutationStatus.Succeeded, (await database.Processor.ProcessAsync(Request([Source()]))).Status);
        var readers = database.Services.GetRequiredService<IDbContextFactory<ReadContext>>();
        await using var reader = await readers.CreateDbContextAsync();
        Assert.Single(await reader.Snapshots.ToListAsync());
        Assert.Empty(reader.ChangeTracker.Entries());
        Assert.Equal(QueryTrackingBehavior.NoTracking, reader.ChangeTracker.QueryTrackingBehavior);
    }

    /// <summary>
    /// Multiple same-ID events in one transaction see prior writes and retain an unseen unpublished revision.
    /// </summary>
    /// <returns>A task completing after committed version/content/publication are checked on a fresh context.</returns>
    [Fact]
    public async Task SameBatchEventsObserveEarlierStateAndRetainUnseenUnpublish()
    {
        await using var database = await fixture.CreateAsync();
        var events = new[] { Source(), Source(version: 3, seconds: 10, type: CmsEventType.Unpublish, json: "{\"title\":\"Unseen\"}") };
        var result = await database.Processor.ProcessAsync(Request(events, "batch-key"));
        Assert.Equal(MutationStatus.Succeeded, result.Status);
        Assert.Equal(new[] { "published", "unpublished" }, result.Success!.Outcomes.Select(outcome => outcome.ReasonCode));
        await using var writer = await database.WriterAsync();
        var row = await writer.Snapshots.SingleAsync();
        Assert.Equal(3, row.Version);
        Assert.False(row.CmsPublished);
        Assert.Contains("Unseen", row.Payload);
        Assert.Equal(Origin.AddSeconds(10), row.CmsTimestamp);
        Assert.Single(await writer.Replays.ToListAsync());
    }

    /// <summary>
    /// A later conflict rolls back an earlier flushed entity and does not reserve its attempted idempotency key.
    /// </summary>
    /// <returns>A task completing after database rollback and subsequent corrected key reuse are verified.</returns>
    [Fact]
    public async Task ConflictRollsBackEarlierWritesAndDoesNotReserveTheKey()
    {
        await using var database = await fixture.CreateAsync();
        await database.Processor.ProcessAsync(Request([Source()]));
        var result = await database.Processor.ProcessAsync(Request([
            Source("new-entity"), Source(seconds: 1, json: "{\"conflict\":true}")], "failed-key"));
        Assert.Equal(MutationStatus.VersionConflict, result.Status);
        Assert.Equal(1, result.EventIndex);
        Assert.Null(result.Success);
        await using (var writer = await database.WriterAsync())
        {
            Assert.Single(await writer.Snapshots.ToListAsync());
            Assert.False(await writer.Snapshots.AnyAsync(row => row.Id == "new-entity"));
            Assert.Empty(await writer.Replays.ToListAsync());
        }
        var corrected = await database.Processor.ProcessAsync(Request([Source("new-entity")], "failed-key"));
        Assert.Equal(MutationStatus.Succeeded, corrected.Status);
        Assert.False(corrected.IsReplay);
    }

    /// <summary>
    /// A restarted application replays the original successful response without restoring historical entity state.
    /// </summary>
    /// <returns>A task completing after byte-equivalent response metadata and later live content are checked.</returns>
    [Fact]
    public async Task SuccessfulReplaySurvivesRestartAndDoesNotRevertLaterState()
    {
        await using var database = await fixture.CreateAsync();
        var request = Request([Source()], "stable-key");
        var first = await database.Processor.ProcessAsync(request);
        await database.Processor.ProcessAsync(Request([Source(version: 2, seconds: 1, json: "{\"title\":\"Later\"}")]));
        await using var restarted = database.CreateServices();
        var replay = await restarted.GetRequiredService<BatchProcessor>().ProcessAsync(request);
        Assert.Equal(MutationStatus.Succeeded, replay.Status);
        Assert.True(replay.IsReplay);
        Assert.Equal(first.Success!.BatchId, replay.Success!.BatchId);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Equal(JsonSerializer.Serialize(first.Success, options), JsonSerializer.Serialize(replay.Success, options));
        await using var writer = await database.WriterAsync();
        Assert.Equal(2, (await writer.Snapshots.SingleAsync()).Version);
    }

    /// <summary>
    /// The same scoped key with different exact body bytes conflicts even when domain events are semantically unchanged.
    /// </summary>
    /// <returns>A task completing after key/body conflict and unchanged durable metadata are verified.</returns>
    [Fact]
    public async Task DifferentBodyUnderAnUnexpiredKeyIsRejected()
    {
        await using var database = await fixture.CreateAsync();
        var events = new[] { Source() };
        var first = await database.Processor.ProcessAsync(Request(events, "body-key"));
        var changedBytes = await database.Processor.ProcessAsync(Request(events, "body-key",
            body: "  " + Encoding.UTF8.GetString(Body(events))));
        Assert.Equal(MutationStatus.IdempotencyConflict, changedBytes.Status);
        Assert.Null(changedBytes.Success);
        await using var writer = await database.WriterAsync();
        Assert.Single(await writer.Replays.ToListAsync());
        var replay = await database.Processor.ProcessAsync(Request([Source()], "body-key"));
        Assert.Equal(first.Success!.BatchId, replay.Success!.BatchId);
    }

    /// <summary>
    /// Principal case and opaque key whitespace remain distinct despite SQL text-padding behavior.
    /// </summary>
    /// <returns>A task completing after independent scoped digests and unique successful results are verified.</returns>
    [Fact]
    public async Task PrincipalScopeAndExactKeySpellingRemainDistinct()
    {
        await using var database = await fixture.CreateAsync();
        var first = await database.Processor.ProcessAsync(Request([Source()], "key"));
        var otherPrincipal = await database.Processor.ProcessAsync(Request([Source()], "key", principal: "CMS-test0001"));
        var otherKey = await database.Processor.ProcessAsync(Request([Source()], "key "));
        Assert.Equal(MutationStatus.Succeeded, otherPrincipal.Status);
        Assert.Equal(MutationStatus.Succeeded, otherKey.Status);
        Assert.False(otherPrincipal.IsReplay);
        Assert.False(otherKey.IsReplay);
        Assert.NotEqual(first.Success!.BatchId, otherPrincipal.Success!.BatchId);
        Assert.NotEqual(first.Success.BatchId, otherKey.Success!.BatchId);
        await using var writer = await database.WriterAsync();
        Assert.Equal(3, await writer.Replays.CountAsync());
    }

    /// <summary>
    /// Racing identical keyed requests commit once and return the same durable batch identifier.
    /// </summary>
    /// <returns>A task completing after both results and single persisted entity/replay effects are verified.</returns>
    [Fact]
    public async Task ConcurrentDuplicateRequestsProduceOneDurableResult()
    {
        await using var database = await fixture.CreateAsync();
        var request = Request([Source()], "race-key");
        var results = await Task.WhenAll(database.Processor.ProcessAsync(request), database.Processor.ProcessAsync(request));
        Assert.All(results, result => Assert.Equal(MutationStatus.Succeeded, result.Status));
        Assert.Equal(results[0].Success!.BatchId, results[1].Success!.BatchId);
        Assert.Single(results, result => result.IsReplay);
        await using var writer = await database.WriterAsync();
        Assert.Single(await writer.Snapshots.ToListAsync());
        Assert.Single(await writer.Replays.ToListAsync());
    }

    /// <summary>
    /// A key expires at seven days and is replaced even when the bounded cleanup cannot reach its expired row.
    /// </summary>
    /// <returns>A task completing after expiration-boundary replacement and updated content are verified.</returns>
    [Fact]
    public async Task ExpiredKeyIsReplacedTransactionallyAtTheBoundary()
    {
        await using var database = await fixture.CreateAsync();
        var first = await database.Processor.ProcessAsync(Request([Source()], "expiry-key"));
        await SeedExpiredAsync(database, 150);
        database.Clock.Now = database.Clock.Now.AddDays(7);
        var replacement = await database.Processor.ProcessAsync(Request([Source(version: 2, seconds: 1)], "expiry-key"));
        Assert.Equal(MutationStatus.Succeeded, replacement.Status);
        Assert.False(replacement.IsReplay);
        Assert.NotEqual(first.Success!.BatchId, replacement.Success!.BatchId);
        await using var writer = await database.WriterAsync();
        Assert.Equal(51, await writer.Replays.CountAsync());
        Assert.Equal(50, await writer.Replays.CountAsync(row => row.ExpiresAt <= database.Clock.Now));
        Assert.Equal(database.Clock.Now.AddDays(7),
            (await writer.Replays.SingleAsync(row => row.ExpiresAt > database.Clock.Now)).ExpiresAt);
        Assert.Equal(2, (await writer.Snapshots.SingleAsync()).Version);
    }

    /// <summary>
    /// Opportunistic cleanup removes at most 100 expired rows, preserving live records and storing no unkeyed response.
    /// </summary>
    /// <returns>A task completing after bounded retention cleanup is verified on actual SQL.</returns>
    [Fact]
    public async Task ExpiredMetadataCleanupIsBoundedAndLeavesLiveKeys()
    {
        await using var database = await fixture.CreateAsync();
        await SeedExpiredAsync(database, 205);
        await database.Processor.ProcessAsync(Request([Source("live")], "live-key"));
        await using (var writer = await database.WriterAsync())
        {
            Assert.Equal(105, await writer.Replays.CountAsync(row => row.ExpiresAt <= database.Clock.Now));
        }
        await database.Processor.ProcessAsync(Request([Source("unkeyed")]));
        await using var observer = await database.WriterAsync();
        Assert.Equal(5, await observer.Replays.CountAsync(row => row.ExpiresAt <= database.Clock.Now));
        Assert.Single(await observer.Replays.Where(row => row.ExpiresAt > database.Clock.Now).ToListAsync());
    }

    /// <summary>
    /// Expiry cleanup rolls back with a conflicted batch instead of committing maintenance independently.
    /// </summary>
    /// <returns>A task completing after expired metadata and original entity state survive rejection.</returns>
    [Fact]
    public async Task ExpiryCleanupRollsBackWhenTheBatchConflicts()
    {
        await using var database = await fixture.CreateAsync();
        await database.Processor.ProcessAsync(Request([Source()]));
        await SeedExpiredAsync(database, 110);
        var result = await database.Processor.ProcessAsync(Request([Source(seconds: 1, json: "{\"different\":true}")]));
        Assert.Equal(MutationStatus.VersionConflict, result.Status);
        await using var writer = await database.WriterAsync();
        Assert.Equal(110, await writer.Replays.CountAsync());
    }

    /// <summary>
    /// Hard deletion removes content/overrides, keeps only its watermark, and a strictly later lifecycle starts enabled.
    /// </summary>
    /// <returns>A task completing after SQL deletion, stale blocking, metadata privacy, and recreation are verified.</returns>
    [Fact]
    public async Task HardDeleteRemovesPayloadAndOverrideWithoutRetainingRequestBodies()
    {
        await using var database = await fixture.CreateAsync();
        const string secret = "CONFIDENTIAL_PAYLOAD_SENTINEL";
        await database.Processor.ProcessAsync(Request([Source(json: "{\"value\":\"" + secret + "\"}")], "publish"));
        Assert.Equal(MutationStatus.Succeeded, await database.Disabler.DisableAsync("article-001"));
        await database.Processor.ProcessAsync(Request([Source(seconds: 2, type: CmsEventType.Delete)], "delete"));
        await using (var writer = await database.WriterAsync())
        {
            Assert.Empty(await writer.Snapshots.ToListAsync());
            Assert.Single(await writer.Deletions.ToListAsync());
            Assert.All(await writer.Replays.ToListAsync(), row => Assert.DoesNotContain(secret, row.ResponseJson));
        }
        var stale = await database.Processor.ProcessAsync(Request([Source(version: 99, seconds: 2)]));
        Assert.Equal("stale", stale.Success!.Outcomes[0].ReasonCode);
        await database.Processor.ProcessAsync(Request([Source(version: 1, seconds: 3)]));
        await using var observer = await database.WriterAsync();
        Assert.False((await observer.Snapshots.SingleAsync()).AdminDisabled);
        Assert.Empty(await observer.Deletions.ToListAsync());
        Assert.DoesNotContain(secret, string.Join("\n", database.Logs.Messages));
    }

    /// <summary>
    /// Binary source-ID collation retains case-distinct entities and rowversion rejects a stale tracked update.
    /// </summary>
    /// <returns>A task completing after case sensitivity and independent SQL concurrency tokens are verified.</returns>
    [Fact]
    public async Task CaseSensitiveIdsAndRowVersionsAreEnforcedBySql()
    {
        await using var database = await fixture.CreateAsync();
        await database.Processor.ProcessAsync(Request([Source("Article"), Source("article")]));
        await using var first = await database.WriterAsync();
        await using var second = await database.WriterAsync();
        Assert.Equal(2, await first.Snapshots.CountAsync());
        var firstRow = await first.Snapshots.SingleAsync(row => row.Id == "Article");
        var staleRow = await second.Snapshots.SingleAsync(row => row.Id == "Article");
        var originalVersion = firstRow.RowVersion.ToArray();
        firstRow.AdminDisabled = true;
        await first.SaveChangesAsync();
        Assert.NotEqual(originalVersion, firstRow.RowVersion);
        staleRow.CmsPublished = false;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
        Assert.Equal(1, firstRow.Version);
    }

    /// <summary>
    /// SQL check constraints reject non-object payloads and nonpositive source revisions even outside application guards.
    /// </summary>
    /// <returns>A task completing after both invalid writes are rejected by the migrated database.</returns>
    [Fact]
    public async Task SqlChecksRejectInvalidContentAndVersions()
    {
        await using var database = await fixture.CreateAsync();
        await using var writer = await database.WriterAsync();
        writer.Snapshots.Add(new SnapshotRow { Id = "invalid-object", Version = 1, Payload = "[]", CmsTimestamp = Origin });
        await Assert.ThrowsAsync<DbUpdateException>(() => writer.SaveChangesAsync());
        writer.ChangeTracker.Clear();
        writer.Snapshots.Add(new SnapshotRow { Id = "invalid-version", Version = 0, Payload = "{}", CmsTimestamp = Origin });
        await Assert.ThrowsAsync<DbUpdateException>(() => writer.SaveChangesAsync());
        writer.ChangeTracker.Clear();
        Assert.Empty(await writer.Snapshots.ToListAsync());
    }

    /// <summary>
    /// Reusing an existing CMS/key digest is rejected by SQL's composite primary key independently of the application lock.
    /// </summary>
    /// <returns>A task completing after the duplicate is rejected and the original metadata remains intact.</returns>
    [Fact]
    public async Task SqlUniquenessProtectsScopedReplayKeys()
    {
        await using var database = await fixture.CreateAsync();
        await database.Processor.ProcessAsync(Request([Source()], "unique-key"));
        await using var writer = await database.WriterAsync();
        var original = await writer.Replays.AsNoTracking().SingleAsync();
        writer.Replays.Add(new ReplayRow
        {
            Principal = original.Principal,
            KeyHash = original.KeyHash.ToArray(),
            BodyHash = original.BodyHash.ToArray(),
            ResponseJson = original.ResponseJson,
            CompletedAt = original.CompletedAt,
            ExpiresAt = original.ExpiresAt
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => writer.SaveChangesAsync());
        writer.ChangeTracker.Clear();
        Assert.Single(await writer.Replays.ToListAsync());
    }

    /// <summary>
    /// Local disabling preserves every CMS field, repeats without a row update, and cannot revive missing/deleted content.
    /// </summary>
    /// <returns>A task completing after persisted override behavior and missing-state results are checked.</returns>
    [Fact]
    public async Task LocalOverrideIsIdempotentAndDoesNotReviveDeletedContent()
    {
        await using var database = await fixture.CreateAsync();
        Assert.Equal(MutationStatus.NotFound, await database.Disabler.DisableAsync("missing"));
        await database.Processor.ProcessAsync(Request([Source(version: 5, seconds: 10)]));
        Assert.Equal(MutationStatus.Succeeded, await database.Disabler.DisableAsync("article-001"));
        byte[] token;
        await using (var observer = await database.WriterAsync())
        {
            var row = await observer.Snapshots.SingleAsync();
            token = row.RowVersion.ToArray();
            Assert.True(row.AdminDisabled);
            Assert.True(row.CmsPublished);
            Assert.Equal(5, row.Version);
            Assert.Equal(Origin.AddSeconds(10), row.CmsTimestamp);
            Assert.Contains("Synthetic", row.Payload);
        }
        Assert.Equal(MutationStatus.Succeeded, await database.Disabler.DisableAsync("article-001"));
        await using (var observer = await database.WriterAsync())
        {
            Assert.Equal(token, (await observer.Snapshots.SingleAsync()).RowVersion);
        }
        await database.Processor.ProcessAsync(Request([Source(seconds: 20, type: CmsEventType.Delete)]));
        Assert.Equal(MutationStatus.NotFound, await database.Disabler.DisableAsync("article-001"));
        await using var final = await database.WriterAsync();
        Assert.Empty(await final.Snapshots.ToListAsync());
        Assert.Single(await final.Deletions.ToListAsync());
    }

    /// <summary>
    /// Seeds only expired successful metadata in the owned test database for bounded-cleanup and rollback checks.
    /// </summary>
    /// <param name="database">The isolated migrated test resource.</param>
    /// <param name="count">The number of expired synthetic rows.</param>
    /// <returns>A task completing after the setup rows are committed.</returns>
    /// <exception cref="DbUpdateException">Synthetic setup cannot be saved.</exception>
    private static async Task SeedExpiredAsync(SqlTestDatabase database, int count)
    {
        await using var writer = await database.WriterAsync();
        for (var index = 0; index < count; index++)
        {
            writer.Replays.Add(new ReplayRow
            {
                Principal = "expired-test",
                KeyHash = SHA256.HashData(Encoding.UTF8.GetBytes(index.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                BodyHash = new byte[32],
                ResponseJson = "{\"batchId\":\"" + Guid.NewGuid() + "\",\"outcomes\":[]}",
                CompletedAt = database.Clock.Now.AddDays(-8),
                ExpiresAt = database.Clock.Now.AddDays(-1)
            });
        }
        await writer.SaveChangesAsync();
    }
}
