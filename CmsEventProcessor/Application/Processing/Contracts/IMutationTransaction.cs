using CmsEventProcessor.Application.Processing.Models;

namespace CmsEventProcessor.Application.Processing.Contracts;

/// <summary>
/// Provides only the state, replay, coordination, and commit operations needed by the two mutation use cases.
/// </summary>
internal interface IMutationTransaction : IAsyncDisposable
{
    /// <summary>
    /// Acquires exclusive mutation ownership before any mutable state or replay lookup is read.
    /// </summary>
    /// <param name="wait">The maximum lock wait within the overall request budget.</param>
    /// <param name="cancellationToken">The request/deadline token.</param>
    /// <returns>True when ownership is acquired; false for an unsuccessful acquisition.</returns>
    /// <exception cref="OperationCanceledException">The request or deadline cancels acquisition.</exception>
    /// <exception cref="System.Data.Common.DbException">The coordination command fails.</exception>
    Task<bool> AcquireAsync(TimeSpan wait, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the latest state inside the transaction after mutation ownership has been acquired.
    /// </summary>
    /// <param name="id">The exact source identifier.</param>
    /// <param name="cancellationToken">The request/deadline token.</param>
    /// <returns>The live snapshot, deletion marker, or empty state for an unknown identifier.</returns>
    /// <exception cref="OperationCanceledException">The request or deadline cancels the query.</exception>
    /// <exception cref="System.Data.Common.DbException">The state cannot be read.</exception>
    Task<StoredEntityState> ReadEntityAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Flushes one proposed state inside the still-uncommitted transaction, making it visible to later batch events.
    /// </summary>
    /// <param name="id">The exact source identifier whose lifecycle changes.</param>
    /// <param name="state">The proposed live or deleted state.</param>
    /// <param name="cancellationToken">The request/deadline token.</param>
    /// <returns>A task completing after state reaches the still-uncommitted transaction.</returns>
    /// <exception cref="OperationCanceledException">The request or deadline cancels the write.</exception>
    /// <exception cref="Exceptions.PersistenceException">A tracked update or concurrency check fails.</exception>
    /// <exception cref="System.Data.Common.DbException">The state cannot be written.</exception>
    Task WriteEntityAsync(string id, StoredEntityState state, CancellationToken cancellationToken);

    /// <summary>
    /// Reads scoped successful replay metadata without loading or retaining submitted batch bodies.
    /// </summary>
    /// <param name="principal">The exact CMS identity.</param>
    /// <param name="keyHash">The digest of the exact opaque key.</param>
    /// <param name="cancellationToken">The request/deadline token.</param>
    /// <returns>The stored result, including expiry, or null when this scoped key is absent.</returns>
    /// <exception cref="OperationCanceledException">The request or deadline cancels the query.</exception>
    /// <exception cref="System.Data.Common.DbException">The metadata cannot be read.</exception>
    Task<ReplayRecord?> ReadReplayAsync(string principal, ReadOnlyMemory<byte> keyHash, CancellationToken cancellationToken);

    /// <summary>
    /// Flushes successful response metadata inside the same uncommitted transaction as entity changes.
    /// </summary>
    /// <param name="record">The scoped hash-only request identity and response metadata.</param>
    /// <param name="cancellationToken">The request/deadline token.</param>
    /// <returns>A task completing after successful replay metadata reaches the transaction.</returns>
    /// <exception cref="OperationCanceledException">The request or deadline cancels the write.</exception>
    /// <exception cref="Exceptions.PersistenceException">The metadata update fails.</exception>
    Task WriteReplayAsync(ReplayRecord record, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a bounded number of expired responses opportunistically within this transaction.
    /// </summary>
    /// <param name="now">The current UTC retention cutoff.</param>
    /// <param name="maximum">The maximum metadata rows to delete.</param>
    /// <param name="cancellationToken">The request/deadline token.</param>
    /// <returns>The number of expired rows removed.</returns>
    /// <exception cref="OperationCanceledException">The request or deadline cancels cleanup.</exception>
    /// <exception cref="System.Data.Common.DbException">Cleanup cannot complete.</exception>
    Task<int> DeleteExpiredAsync(DateTimeOffset now, int maximum, CancellationToken cancellationToken);

    /// <summary>
    /// Commits all proposed state and metadata changes without automatic transaction replay.
    /// </summary>
    /// <param name="cancellationToken">The request/deadline token, including the commit acknowledgment period.</param>
    /// <returns>A task completing only after commit acknowledgment succeeds.</returns>
    /// <exception cref="OperationCanceledException">Cancellation interrupts commit acknowledgment, leaving its outcome potentially ambiguous.</exception>
    /// <exception cref="System.Data.Common.DbException">Commit fails or its acknowledgment is lost.</exception>
    Task CommitAsync(CancellationToken cancellationToken);
}
