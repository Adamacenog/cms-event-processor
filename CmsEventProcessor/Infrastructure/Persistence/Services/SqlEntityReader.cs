using System.Data.Common;
using System.Linq.Expressions;
using System.Text.Json;
using CmsEventProcessor.Application.Entities.Contracts;
using CmsEventProcessor.Application.Entities.Exceptions;
using CmsEventProcessor.Application.Entities.Models;
using CmsEventProcessor.Application.Processing.Options;
using CmsEventProcessor.Domain.Entities;
using CmsEventProcessor.Infrastructure.Persistence.Contexts;
using CmsEventProcessor.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CmsEventProcessor.Infrastructure.Persistence.Services;

/// <summary>
/// Executes no-tracking, projected SQL reads with visibility/seek filtering before a bounded limit and one cooperative budget.
/// </summary>
/// <param name="contexts">The independently configured read-context factory.</param>
/// <param name="options">The validated total operation timeout, shared with processing configuration.</param>
/// <param name="logger">The logger receiving safe read correlations and failure types, never source values.</param>
internal sealed class SqlEntityReader(IDbContextFactory<ReadContext> contexts, IOptions<ProcessingOptions> options,
    ILogger<SqlEntityReader> logger) : IEntityReader
{
    private static readonly Expression<Func<SnapshotRow, EntityReadProjection>> Projection = row =>
        new EntityReadProjection(row.Id, row.Payload, row.Version, row.CmsTimestamp, row.CmsPublished, row.AdminDisabled);

    /// <summary>
    /// Fetches at most pageSize plus one projected rows, using the source column's binary collation for ordering and continuation.
    /// </summary>
    /// <param name="includeHidden">The server-selected administrator visibility flag.</param>
    /// <param name="pageSize">The requested count, from one to one hundred.</param>
    /// <param name="afterId">The exact continuation identifier, or null.</param>
    /// <param name="cancellationToken">The caller-abort token.</param>
    /// <returns>A bounded page whose next identifier is present only when another matching row exists.</returns>
    /// <exception cref="ArgumentException">The size or identifier violates input invariants.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels.</exception>
    /// <exception cref="EntityReadException">The database read fails or its deadline expires.</exception>
    public Task<EntityPage> ListAsync(bool includeHidden, int pageSize, string? afterId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 100);
        if (afterId is not null) EntityRules.ValidateId(afterId);
        return ExecuteAsync(async (context, token) =>
        {
            var query = Visible(context, includeHidden);
            if (afterId is not null) query = query.Where(row => string.Compare(row.Id, afterId) > 0);
            var rows = await query.OrderBy(row => row.Id).Select(Projection).Take(pageSize + 1).ToListAsync(token);
            var items = rows.Take(pageSize).Select(Materialize).ToArray();
            return new EntityPage(items, rows.Count > pageSize ? items[^1].Id : null);
        }, cancellationToken);
    }

    /// <summary>
    /// Reads one visible projection without tracking or consulting deletion/replay tables.
    /// </summary>
    /// <param name="id">The exact validated identifier.</param>
    /// <param name="includeHidden">The server-selected administrator visibility flag.</param>
    /// <param name="cancellationToken">The caller-abort token.</param>
    /// <returns>The matching visible snapshot or null.</returns>
    /// <exception cref="ArgumentException">The identifier violates input invariants.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels.</exception>
    /// <exception cref="EntityReadException">The database read fails or its deadline expires.</exception>
    public Task<EntityDetails?> FindAsync(string id, bool includeHidden, CancellationToken cancellationToken)
    {
        EntityRules.ValidateId(id);
        return ExecuteAsync(async (context, token) =>
        {
            var row = await Visible(context, includeHidden).Where(row => row.Id == id).Select(Projection).SingleOrDefaultAsync(token);
            return row is null ? null : Materialize(row);
        }, cancellationToken);
    }

    /// <summary>
    /// Applies the same database-side visibility predicate for listing and lookup.
    /// </summary>
    /// <param name="context">The fresh read-only context.</param>
    /// <param name="includeHidden">True for administrators, false for normal readers.</param>
    /// <returns>An unexecuted no-tracking query with the required visibility restriction.</returns>
    private static IQueryable<SnapshotRow> Visible(ReadContext context, bool includeHidden) => includeHidden ?
        context.Snapshots.AsNoTracking() : context.Snapshots.AsNoTracking().Where(row => row.CmsPublished && !row.AdminDisabled);

    /// <summary>
    /// Clones valid current content after SQL applies filtering/projection/limits, leaving no dependency on the JSON document lifetime.
    /// </summary>
    /// <param name="row">One selected SQL projection.</param>
    /// <returns>An independently owned application read model.</returns>
    /// <exception cref="JsonException">Stored content violates the expected JSON invariant.</exception>
    private static EntityDetails Materialize(EntityReadProjection row)
    {
        using var document = JsonDocument.Parse(row.Payload);
        return new EntityDetails(row.Id, document.RootElement, row.Version, row.Timestamp, row.Published, row.Disabled);
    }

    /// <summary>
    /// Opens one read context under a linked deadline and preserves caller cancellation without implicit retries.
    /// </summary>
    /// <typeparam name="T">The bounded read result type.</typeparam>
    /// <param name="query">The query operation receiving the fresh context and one linked token.</param>
    /// <param name="caller">The originating caller-abort token.</param>
    /// <returns>The completed bounded result.</returns>
    /// <exception cref="OperationCanceledException">The originating caller cancels.</exception>
    /// <exception cref="EntityReadException">Storage fails or the operation budget expires.</exception>
    private async Task<T> ExecuteAsync<T>(Func<ReadContext, CancellationToken, Task<T>> query, CancellationToken caller)
    {
        using var deadline = new CancellationTokenSource(options.Value.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, deadline.Token);
        try
        {
            await using var context = await contexts.CreateDbContextAsync(linked.Token);
            return await query(context, linked.Token);
        }
        catch (Exception exception) when (exception is DbException or OperationCanceledException)
        {
            caller.ThrowIfCancellationRequested();
            logger.LogWarning("Entity read {Correlation} failed; deadline expired: {TimedOut}; failure type {FailureType}.",
                Guid.NewGuid(), deadline.IsCancellationRequested, exception.GetType().Name);
            throw new EntityReadException(deadline.IsCancellationRequested, exception);
        }
    }
}
