using System.Data.Common;
using CmsEventProcessor.Application.Processing.Contracts;
using CmsEventProcessor.Application.Processing.Exceptions;
using CmsEventProcessor.Application.Processing.Models;
using CmsEventProcessor.Application.Processing.Options;
using Microsoft.Extensions.Logging;

namespace CmsEventProcessor.Application.Processing.Services;

/// <summary>
/// Owns one deadline, transaction, and commit-attempt boundary shared by ingestion and local disabling.
/// </summary>
internal sealed class MutationAttempt : IAsyncDisposable
{
    private readonly CancellationTokenSource deadline;
    private readonly CancellationTokenSource linked;
    private readonly ILogger logger;
    private readonly CancellationToken caller;
    private IMutationTransaction? transaction;
    private bool commitAttempted;

    /// <summary>
    /// Starts a single budget that is not reset between database operations.
    /// </summary>
    /// <param name="options">The validated timeout and lock configuration.</param>
    /// <param name="caller">The originating request cancellation token.</param>
    /// <param name="logger">The logger receiving safe statuses and exception types only.</param>
    public MutationAttempt(ProcessingOptions options, CancellationToken caller, ILogger logger)
    {
        deadline = new CancellationTokenSource(options.Timeout);
        linked = CancellationTokenSource.CreateLinkedTokenSource(caller, deadline.Token);
        this.caller = caller;
        this.logger = logger;
    }

    public CancellationToken Token => linked.Token;

    /// <summary>
    /// Opens request-owned storage before the first coordinated read.
    /// </summary>
    /// <param name="factory">The configured infrastructure transaction factory.</param>
    /// <returns>The newly opened transaction.</returns>
    /// <exception cref="OperationCanceledException">The caller or processing budget cancels startup.</exception>
    /// <exception cref="DbException">The database cannot begin a transaction.</exception>
    public async Task<IMutationTransaction> OpenAsync(IMutationTransactionFactory factory)
    {
        transaction = await factory.BeginAsync(Token);
        return transaction;
    }

    /// <summary>
    /// Marks the ambiguous period before awaiting the provider's commit acknowledgment.
    /// </summary>
    /// <returns>A task completing after commit is acknowledged.</returns>
    /// <exception cref="OperationCanceledException">Cancellation interrupts acknowledgment.</exception>
    /// <exception cref="DbException">The provider cannot confirm commit.</exception>
    /// <exception cref="InvalidOperationException">No transaction was opened.</exception>
    public async Task CommitAsync()
    {
        if (transaction is null)
        {
            throw new InvalidOperationException("A transaction must be opened before commit.");
        }
        Token.ThrowIfCancellationRequested();
        commitAttempted = true;
        await transaction.CommitAsync(Token);
    }

    /// <summary>
    /// Recognizes operational/cancellation failures without catching programming errors as expected client results.
    /// </summary>
    /// <param name="exception">The operation failure to classify.</param>
    /// <returns>True for provider, persistence, cancellation, or transport-I/O failures.</returns>
    public static bool IsOperationalFailure(Exception exception) =>
        exception is DbException or PersistenceException or OperationCanceledException or IOException;

    /// <summary>
    /// Preserves caller cancellation and never claims guaranteed rollback after a commit attempt.
    /// </summary>
    /// <param name="exception">The recognized failure, logged by type without its message or contents.</param>
    /// <param name="correlation">The safe operation correlation identifier.</param>
    /// <returns>CommitUnknown after a commit attempt, TimedOut for an elapsed deadline, or StorageUnavailable.</returns>
    /// <exception cref="OperationCanceledException">The originating caller canceled the operation.</exception>
    public MutationStatus ClassifyFailure(Exception exception, Guid correlation)
    {
        if (caller.IsCancellationRequested)
        {
            logger.LogInformation("Mutation {Correlation} canceled by caller; commit attempted: {CommitAttempted}.",
                correlation, commitAttempted);
            caller.ThrowIfCancellationRequested();
        }
        var status = commitAttempted ? MutationStatus.CommitUnknown :
            deadline.IsCancellationRequested ? MutationStatus.TimedOut : MutationStatus.StorageUnavailable;
        logger.LogWarning("Mutation {Correlation} failed with {Status}; failure type {FailureType}. No automatic retry.",
            correlation, status, exception.GetType().Name);
        return status;
    }

    /// <summary>
    /// Releases transaction resources before disposing token sources; known storage cleanup failures do not replace an established result.
    /// </summary>
    /// <returns>A value task completing after transaction cleanup and cancellation-source disposal.</returns>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            logger.LogWarning("Mutation cleanup failed with {FailureType}; connection disposal was attempted.", exception.GetType().Name);
        }
        finally
        {
            linked.Dispose();
            deadline.Dispose();
        }
    }
}
