using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using CmsEventProcessor.Application.Observability;
using CmsEventProcessor.Application.Processing.Contracts;
using CmsEventProcessor.Application.Processing.Exceptions;
using CmsEventProcessor.Application.Processing.Models;
using CmsEventProcessor.Domain.Entities.Models;
using CmsEventProcessor.Infrastructure.Persistence.Contexts;
using CmsEventProcessor.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CmsEventProcessor.Infrastructure.Persistence.Services;

/// <summary>
/// Implements transaction-owned SQL coordination, state writes, and scoped successful replay storage.
/// </summary>
/// <param name="context">The dedicated writer context disposed with this transaction.</param>
/// <param name="transaction">The transaction owning every operation and the application lock.</param>
/// <param name="logger">The logger receiving cleanup failure types without SQL values or exception messages.</param>
/// <param name="telemetry">The fixed-name transaction/lock instruments.</param>
internal sealed class SqlMutationTransaction(WriteContext context, IDbContextTransaction transaction,
    ILogger<SqlMutationTransaction> logger, OperationTelemetry telemetry) : IMutationTransaction
{
    public const string LockResource = "CmsEventProcessor:Mutation:v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private bool ownsMutation;
    private bool committed;
    private bool commitAttempted;
    private bool lockAttempted;
    private int? lockCode;
    private double lockSeconds;
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly OperationObservation observation = telemetry.Begin("transaction", CancellationToken.None);

    /// <summary>
    /// Checks every application-lock return code and refuses state access after unsuccessful acquisition.
    /// </summary>
    /// <param name="wait">The bounded lock wait.</param>
    /// <param name="cancellationToken">The request/deadline token.</param>
    /// <returns>True only for nonnegative SQL success codes.</returns>
    /// <exception cref="OperationCanceledException">Acquisition is canceled.</exception>
    /// <exception cref="DbException">The coordination command fails.</exception>
    public async Task<bool> AcquireAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandTimeout = context.Database.GetCommandTimeout() ?? 30;
        command.CommandText = """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = 'Exclusive',
                @LockOwner = 'Transaction', @LockTimeout = @wait;
            SELECT @result;
            """;
        var resource = command.CreateParameter();
        resource.ParameterName = "@resource";
        resource.Value = LockResource;
        command.Parameters.Add(resource);
        var timeout = command.CreateParameter();
        timeout.ParameterName = "@wait";
        timeout.Value = (int)Math.Clamp(Math.Ceiling(wait.TotalMilliseconds), 0, int.MaxValue);
        command.Parameters.Add(timeout);
        lockAttempted = true;
        var lockStarted = Stopwatch.GetTimestamp();
        object? result;
        try { result = await command.ExecuteScalarAsync(cancellationToken); }
        finally { lockSeconds = Stopwatch.GetElapsedTime(lockStarted).TotalSeconds; }
        lockCode = result is int code ? code : null;
        ownsMutation = lockCode >= 0;
        return ownsMutation;
    }

    /// <summary>
    /// Reconstructs domain state from the primary transaction without exposing EF row objects.
    /// </summary>
    /// <param name="id">The exact identifier compared with the schema's binary collation.</param>
    /// <param name="cancellationToken">The request/deadline token.</param>
    /// <returns>A reconstructed snapshot, marker, or unknown state.</returns>
    /// <exception cref="InvalidOperationException">Mutation ownership was not acquired.</exception>
    /// <exception cref="OperationCanceledException">The query is canceled.</exception>
    /// <exception cref="DbException">State cannot be read.</exception>
    public async Task<StoredEntityState> ReadEntityAsync(string id, CancellationToken cancellationToken)
    {
        RequireOwnership();
        var row = await context.Snapshots.AsNoTracking().SingleOrDefaultAsync(value => value.Id == id, cancellationToken);
        var marker = await context.Deletions.AsNoTracking().SingleOrDefaultAsync(value => value.Id == id, cancellationToken);
        EntitySnapshot? snapshot = null;
        if (row is not null)
        {
            using var payload = JsonDocument.Parse(row.Payload);
            snapshot = new EntitySnapshot(row.Id, row.Version, new EntityPayload(payload.RootElement),
                row.CmsTimestamp, row.CmsPublished, row.AdminDisabled);
        }
        return new StoredEntityState(snapshot, marker is null ? null : new DeletionMarker(marker.Id, marker.Timestamp));
    }

    /// <summary>
    /// Flushes proposed state within the open transaction so later same-ID events observe it immediately.
    /// </summary>
    /// <param name="id">The lifecycle identifier being changed.</param>
    /// <param name="state">Either live content or a content-free deletion marker.</param>
    /// <param name="cancellationToken">The request/deadline token.</param>
    /// <returns>A task completing after state is flushed within the open transaction.</returns>
    /// <exception cref="InvalidOperationException">Ownership or proposed-state invariants are violated.</exception>
    /// <exception cref="OperationCanceledException">The write is canceled.</exception>
    /// <exception cref="PersistenceException">A tracked update or row-version check fails.</exception>
    public async Task WriteEntityAsync(string id, StoredEntityState state, CancellationToken cancellationToken)
    {
        RequireOwnership();
        if ((state.Snapshot is null) == (state.Deletion is null) ||
            state.Snapshot is not null && !string.Equals(id, state.Snapshot.Id, StringComparison.Ordinal) ||
            state.Deletion is not null && !string.Equals(id, state.Deletion.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A state write must contain exactly one matching lifecycle representation.");
        }
        var row = await context.Snapshots.SingleOrDefaultAsync(value => value.Id == id, cancellationToken);
        var marker = await context.Deletions.SingleOrDefaultAsync(value => value.Id == id, cancellationToken);
        if (state.Snapshot is { } snapshot)
        {
            if (marker is not null)
            {
                context.Deletions.Remove(marker);
            }
            if (row is null)
            {
                row = new SnapshotRow { Id = id };
                context.Snapshots.Add(row);
            }
            row.Payload = snapshot.Payload.Value.GetRawText();
            row.Version = snapshot.Version;
            row.CmsTimestamp = snapshot.CmsTimestamp;
            row.CmsPublished = snapshot.CmsPublished;
            row.AdminDisabled = snapshot.AdminDisabled;
        }
        else
        {
            if (row is not null)
            {
                context.Snapshots.Remove(row);
            }
            if (marker is null)
            {
                marker = new DeletionRow { Id = id };
                context.Deletions.Add(marker);
            }
            marker.Timestamp = state.Deletion!.Timestamp;
        }
        await FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Looks up scoped key digests without SQL text-padding or case normalization of opaque keys.
    /// </summary>
    /// <param name="principal">The exact CMS principal.</param>
    /// <param name="keyHash">The SHA-256 key digest.</param>
    /// <param name="cancellationToken">The request/deadline token.</param>
    /// <returns>The stored successful result and expiry, or null.</returns>
    /// <exception cref="InvalidOperationException">Ownership or stored metadata invariants are violated.</exception>
    /// <exception cref="OperationCanceledException">The query is canceled.</exception>
    /// <exception cref="DbException">Metadata cannot be read.</exception>
    public async Task<ReplayRecord?> ReadReplayAsync(string principal, ReadOnlyMemory<byte> keyHash, CancellationToken cancellationToken)
    {
        RequireOwnership();
        var hash = keyHash.ToArray();
        var row = await context.Replays.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Principal == principal && value.KeyHash == hash, cancellationToken);
        if (row is null)
        {
            return null;
        }
        var response = JsonSerializer.Deserialize<BatchSuccess>(row.ResponseJson, JsonOptions)
            ?? throw new InvalidOperationException("Stored replay metadata is invalid.");
        return new ReplayRecord(row.Principal, row.KeyHash, row.BodyHash, response, row.CompletedAt, row.ExpiresAt);
    }

    /// <summary>
    /// Inserts or replaces an expired scoped result before committing entity and response changes together.
    /// </summary>
    /// <param name="record">The successful metadata and exact-body digest.</param>
    /// <param name="cancellationToken">The request/deadline token.</param>
    /// <returns>A task completing after the successful response is flushed within the transaction.</returns>
    /// <exception cref="InvalidOperationException">Ownership was not acquired.</exception>
    /// <exception cref="OperationCanceledException">The write is canceled.</exception>
    /// <exception cref="PersistenceException">The tracked update fails.</exception>
    public async Task WriteReplayAsync(ReplayRecord record, CancellationToken cancellationToken)
    {
        RequireOwnership();
        var key = record.KeyHash.ToArray();
        var row = await context.Replays.SingleOrDefaultAsync(value =>
            value.Principal == record.Principal && value.KeyHash == key, cancellationToken);
        if (row is null)
        {
            row = new ReplayRow { Principal = record.Principal, KeyHash = key };
            context.Replays.Add(row);
        }
        row.BodyHash = record.BodyHash.ToArray();
        row.ResponseJson = JsonSerializer.Serialize(record.Response, JsonOptions);
        row.CompletedAt = record.CompletedAt;
        row.ExpiresAt = record.ExpiresAt;
        await FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Removes only a bounded number of expired responses during the request's transaction.
    /// </summary>
    /// <param name="now">The UTC expiry cutoff.</param>
    /// <param name="maximum">The maximum number of metadata rows to remove.</param>
    /// <param name="cancellationToken">The request/deadline token.</param>
    /// <returns>The number removed within this transaction.</returns>
    /// <exception cref="InvalidOperationException">Ownership was not acquired.</exception>
    /// <exception cref="OperationCanceledException">Cleanup is canceled.</exception>
    /// <exception cref="DbException">The delete command fails.</exception>
    public Task<int> DeleteExpiredAsync(DateTimeOffset now, int maximum, CancellationToken cancellationToken)
    {
        RequireOwnership();
        return context.Replays.Where(row => row.ExpiresAt <= now).OrderBy(row => row.ExpiresAt)
            .Take(maximum).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Confirms commit once and leaves uncertain acknowledgments for conservative application classification.
    /// </summary>
    /// <param name="cancellationToken">The original request/deadline token.</param>
    /// <returns>A task completing only after successful commit acknowledgment.</returns>
    /// <exception cref="OperationCanceledException">Acknowledgment is canceled.</exception>
    /// <exception cref="DbException">Commit cannot be confirmed.</exception>
    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        RequireOwnership();
        commitAttempted = true;
        await transaction.CommitAsync(cancellationToken);
        committed = true;
    }

    /// <summary>
    /// Bounds explicit rollback cleanup and always closes the context, including after ambiguous acknowledgment.
    /// </summary>
    /// <returns>A value task completing after transaction and connection disposal.</returns>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!committed)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await transaction.RollbackAsync(cleanup.Token);
                }
                catch (Exception exception) when (exception is DbException or OperationCanceledException or InvalidOperationException)
                {
                    logger.LogWarning("Rollback cleanup was not confirmed; failure type {FailureType}. Closing connection.",
                        exception.GetType().Name);
                }
            }
        }
        finally
        {
            try
            {
                await transaction.DisposeAsync();
            }
            finally
            {
                try { await context.DisposeAsync(); }
                finally
                {
                    telemetry.RecordTransaction(Stopwatch.GetElapsedTime(started).TotalSeconds, lockSeconds, lockCode, commitAttempted, committed, lockAttempted);
                    observation.SetOutcome(committed ? "Succeeded" : commitAttempted ? "CommitUnknown" : "RolledBack");
                    observation.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Flushes writes without committing and removes stale tracking before subsequent batch events.
    /// </summary>
    /// <param name="cancellationToken">The request/deadline token.</param>
    /// <returns>A task completing after transactional writes.</returns>
    /// <exception cref="PersistenceException">EF reports an update or concurrency failure.</exception>
    /// <exception cref="OperationCanceledException">The write is canceled.</exception>
    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            context.ChangeTracker.Clear();
        }
        catch (DbUpdateException exception)
        {
            throw new PersistenceException(exception);
        }
    }

    /// <summary>
    /// Rejects accidental storage access outside the shared global coordination protocol.
    /// </summary>
    /// <exception cref="InvalidOperationException">The transaction does not own the mutation lock.</exception>
    private void RequireOwnership()
    {
        if (!ownsMutation)
        {
            throw new InvalidOperationException("Mutation ownership is required before state access.");
        }
    }
}
