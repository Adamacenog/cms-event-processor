using CmsEventProcessor.Application.Entities.Exceptions;
using CmsEventProcessor.Application.Entities.Models;

namespace CmsEventProcessor.Application.Entities.Contracts;

/// <summary>
/// Provides bounded current-snapshot reads with server-selected visibility and no persistence or HTTP types in the contract.
/// </summary>
internal interface IEntityReader
{
    /// <summary>
    /// Reads a page strictly after an optional source identifier, applying visibility before the page limit.
    /// </summary>
    /// <param name="includeHidden">True only for a server-authenticated administrator.</param>
    /// <param name="pageSize">The validated page size between one and one hundred.</param>
    /// <param name="afterId">The decoded continuation identifier, or null for the first page.</param>
    /// <param name="cancellationToken">The caller-abort token.</param>
    /// <returns>The bounded page and a continuation identifier only when another matching row exists.</returns>
    /// <exception cref="ArgumentException">Paging inputs violate application invariants.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels the read.</exception>
    /// <exception cref="EntityReadException">Storage is unavailable or the read budget expires.</exception>
    Task<EntityPage> ListAsync(bool includeHidden, int pageSize, string? afterId, CancellationToken cancellationToken);

    /// <summary>
    /// Finds one current snapshot under the same visibility policy as listing.
    /// </summary>
    /// <param name="id">The validated exact source identifier.</param>
    /// <param name="includeHidden">True only for a server-authenticated administrator.</param>
    /// <param name="cancellationToken">The caller-abort token.</param>
    /// <returns>The visible snapshot, or null for missing, deleted, or hidden state.</returns>
    /// <exception cref="ArgumentException">The identifier violates application invariants.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels the read.</exception>
    /// <exception cref="EntityReadException">Storage is unavailable or the read budget expires.</exception>
    Task<EntityDetails?> FindAsync(string id, bool includeHidden, CancellationToken cancellationToken);
}
