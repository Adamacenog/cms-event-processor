namespace CmsEventProcessor.Application.Processing.Contracts;

/// <summary>
/// Opens an isolated request-owned write transaction without exposing a SQL connection or EF context.
/// </summary>
internal interface IMutationTransactionFactory
{
    /// <summary>
    /// Opens the database connection and begins a transaction under the request's overall cancellation budget.
    /// </summary>
    /// <param name="cancellationToken">The linked request/deadline token used for connection and transaction creation.</param>
    /// <returns>A transaction whose disposal releases its resources and rolls back uncommitted work.</returns>
    /// <exception cref="OperationCanceledException">The request is canceled or its processing deadline expires.</exception>
    /// <exception cref="System.Data.Common.DbException">The database cannot open or begin a transaction.</exception>
    Task<IMutationTransaction> BeginAsync(CancellationToken cancellationToken);
}
