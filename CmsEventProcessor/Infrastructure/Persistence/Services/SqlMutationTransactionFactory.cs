using CmsEventProcessor.Application.Processing.Contracts;
using CmsEventProcessor.Infrastructure.Persistence.Contexts;
using Microsoft.EntityFrameworkCore;

namespace CmsEventProcessor.Infrastructure.Persistence.Services;

/// <summary>
/// Gives each mutation a fresh context and transaction, independent of prior requests or tracking state.
/// </summary>
/// <param name="contexts">The configured writer context factory.</param>
/// <param name="logger">The safe cleanup diagnostic logger.</param>
internal sealed class SqlMutationTransactionFactory(IDbContextFactory<WriteContext> contexts,
    ILogger<SqlMutationTransaction> logger) : IMutationTransactionFactory
{
    /// <summary>
    /// Begins a cancellation-aware transaction without executing migrations or retrying failed work.
    /// </summary>
    /// <param name="cancellationToken">The request/deadline token used throughout startup.</param>
    /// <returns>The request-owned SQL mutation transaction.</returns>
    /// <exception cref="OperationCanceledException">The caller or deadline cancels startup.</exception>
    /// <exception cref="System.Data.Common.DbException">The connection or transaction cannot open.</exception>
    public async Task<IMutationTransaction> BeginAsync(CancellationToken cancellationToken)
    {
        var context = await contexts.CreateDbContextAsync(cancellationToken);
        try
        {
            var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            return new SqlMutationTransaction(context, transaction, logger);
        }
        catch
        {
            await context.DisposeAsync();
            throw;
        }
    }
}
