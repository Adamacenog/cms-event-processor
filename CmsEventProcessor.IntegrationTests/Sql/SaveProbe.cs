using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CmsEventProcessor.IntegrationTests.Sql;

/// <summary>
/// Pauses a selected transactional write after earlier batch effects reached SQL but before commit.
/// </summary>
internal sealed class SaveProbe : SaveChangesInterceptor
{
    private int count;
    public int PauseAt { get; init; } = 2;
    public int SessionId { get; private set; }
    public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Signals a known SQL session and pauses until cancellation or explicit test release.
    /// </summary>
    /// <param name="eventData">The active writer context and diagnostic metadata.</param>
    /// <param name="result">The current save interception result.</param>
    /// <param name="cancellationToken">The original request/deadline token.</param>
    /// <returns>The unchanged save result after release.</returns>
    /// <exception cref="OperationCanceledException">The paused write is canceled.</exception>
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref count) == PauseAt)
        {
            SessionId = ((SqlConnection)eventData.Context!.Database.GetDbConnection()).ServerProcessId;
            Reached.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
        return result;
    }
}
