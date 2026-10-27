using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CmsEventProcessor.IntegrationTests.Sql;

/// <summary>
/// Pauses before commit or drops its acknowledgment after the real database commit for deterministic boundary tests.
/// </summary>
internal sealed class CommitProbe : DbTransactionInterceptor
{
    public bool Pause { get; init; }
    public bool LoseAcknowledgment { get; init; }
    public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Signals the precommit boundary and optionally waits for explicit release under the original request token.
    /// </summary>
    /// <param name="transaction">The real transaction being committed.</param>
    /// <param name="eventData">The EF transaction metadata.</param>
    /// <param name="result">The current interception result.</param>
    /// <param name="cancellationToken">The original request/deadline token.</param>
    /// <returns>The unchanged interception result after release.</returns>
    /// <exception cref="OperationCanceledException">The request/deadline cancels a paused commit.</exception>
    public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
        TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        Reached.TrySetResult();
        if (Pause)
        {
            await Release.Task.WaitAsync(cancellationToken);
        }
        return result;
    }

    /// <summary>
    /// Simulates a lost acknowledgment only after SQL has actually committed state and replay metadata.
    /// </summary>
    /// <param name="transaction">The completed real transaction.</param>
    /// <param name="eventData">The EF completion metadata.</param>
    /// <param name="cancellationToken">The request/deadline token.</param>
    /// <returns>A completed task when acknowledgment is retained.</returns>
    /// <exception cref="IOException">The test intentionally drops the postcommit acknowledgment.</exception>
    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (LoseAcknowledgment)
        {
            throw new IOException("CONFIDENTIAL_COMMIT_SENTINEL");
        }
        return Task.CompletedTask;
    }
}
