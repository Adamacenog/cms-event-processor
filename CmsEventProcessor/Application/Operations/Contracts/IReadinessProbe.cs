namespace CmsEventProcessor.Application.Operations.Contracts;

/// <summary>
/// Checks current read/write storage and expected schema availability without changing the database or exposing dependency details.
/// </summary>
internal interface IReadinessProbe
{
    /// <summary>
    /// Verifies the configured storage under a cooperative readiness budget.
    /// </summary>
    /// <param name="cancellationToken">The requesting caller's abort token.</param>
    /// <returns>True only when required schema and both connections are available.</returns>
    /// <exception cref="OperationCanceledException">The caller aborts the probe.</exception>
    Task<bool> CheckAsync(CancellationToken cancellationToken);
}
