using System.Data.Common;
using CmsEventProcessor.Application.Operations.Contracts;
using CmsEventProcessor.Application.Operations.Options;
using CmsEventProcessor.Infrastructure.Persistence.Contexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CmsEventProcessor.Infrastructure.Persistence.Services;

/// <summary>
/// Verifies writer migration history and actual read/write column availability with no database mutation or background polling.
/// </summary>
/// <param name="writers">The independently configured writer factory.</param>
/// <param name="readers">The independently configured reader factory.</param>
/// <param name="options">The bounded cooperative readiness budget.</param>
/// <param name="logger">The logger receiving only safe dependency failure types.</param>
internal sealed class SqlReadinessProbe(IDbContextFactory<WriteContext> writers, IDbContextFactory<ReadContext> readers,
    IOptions<OperationsOptions> options, ILogger<SqlReadinessProbe> logger) : IReadinessProbe
{
    private const string SnapshotQuery = "SELECT TOP(0) [Id],[Payload],[Version],[CmsTimestamp],[CmsPublished],[AdminDisabled],[RowVersion] FROM [Snapshots];";

    /// <summary>
    /// Checks both live connections and expected writer history/schema under one deadline; failed domain requests do not influence readiness.
    /// </summary>
    /// <param name="cancellationToken">The requesting caller token.</param>
    /// <returns>True only for current history and successful actual zero-row schema reads.</returns>
    /// <exception cref="OperationCanceledException">The caller aborts the probe.</exception>
    public async Task<bool> CheckAsync(CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(options.Value.ReadinessTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            await using var write = await writers.CreateDbContextAsync(linked.Token);
            var expected = write.Database.GetMigrations().ToArray();
            var applied = (await write.Database.GetAppliedMigrationsAsync(linked.Token)).ToArray();
            if (expected.Length == 0 || !expected.SequenceEqual(applied, StringComparer.Ordinal)) return false;
            await VerifyColumnsAsync(write, SnapshotQuery +
                "SELECT TOP(0) [Id],[Timestamp] FROM [DeletionMarkers];" +
                "SELECT TOP(0) [Principal],[KeyHash],[BodyHash],[ResponseJson],[CompletedAt],[ExpiresAt] FROM [ReplayResults];", linked.Token);
            await using var read = await readers.CreateDbContextAsync(linked.Token);
            await VerifyColumnsAsync(read, SnapshotQuery, linked.Token);
            return true;
        }
        catch (Exception exception) when (exception is DbException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogWarning("Readiness unavailable; failure type {FailureType}.", exception.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// Executes and advances every zero-row result so missing columns/tables cannot be hidden in an unread batch result.
    /// </summary>
    /// <param name="context">The fresh production context.</param>
    /// <param name="sql">The fixed schema query containing no data values.</param>
    /// <param name="token">The shared readiness/caller token.</param>
    /// <returns>A task completing after all required columns are verified.</returns>
    /// <exception cref="DbException">A connection, table, column, or required privilege is unavailable.</exception>
    /// <exception cref="OperationCanceledException">The caller or readiness budget cancels verification.</exception>
    private async Task VerifyColumnsAsync(DbContext context, string sql, CancellationToken token)
    {
        await context.Database.OpenConnectionAsync(token);
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = (int)Math.Ceiling(options.Value.ReadinessTimeout.TotalSeconds);
        await using var result = await command.ExecuteReaderAsync(token);
        while (await result.NextResultAsync(token)) { }
    }
}
