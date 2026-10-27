using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CmsEventProcessor.IntegrationTests.Entities;

/// <summary>
/// Captures parameterized SQL shape without values and optionally pauses real reads for deterministic deadline/abort checks.
/// </summary>
internal sealed class ReadProbe : DbCommandInterceptor
{
    public bool Pause { get; init; }
    public ConcurrentQueue<string> Commands { get; } = new();
    public ConcurrentQueue<int> IntegerParameters { get; } = new();
    public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Records schema/query structure only, and optionally waits until the original read token is canceled.
    /// </summary>
    /// <param name="command">The real SQL command, whose confidential parameter values are never copied.</param>
    /// <param name="eventData">The EF read command metadata.</param>
    /// <param name="result">The current interception result.</param>
    /// <param name="cancellationToken">The originating linked read token.</param>
    /// <returns>The unchanged result after normal observation.</returns>
    /// <exception cref="OperationCanceledException">A paused read reaches its deadline or caller abortion.</exception>
    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Commands.Enqueue(command.CommandText);
        foreach (DbParameter parameter in command.Parameters)
            if (parameter.Value is int count) IntegerParameters.Enqueue(count);
        Reached.TrySetResult();
        if (Pause) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return result;
    }
}
