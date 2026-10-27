using CmsEventProcessor.Application.Operations.Contracts;

namespace CmsEventProcessor.UnitTests.Operations;

/// <summary>
/// Signals shutdown at a deterministic point during an otherwise successful readiness probe.
/// </summary>
/// <param name="lifetime">The controllable host lifetime.</param>
internal sealed class StoppingReadinessProbe(TestHostLifetime lifetime) : IReadinessProbe
{
    public int Calls { get; private set; }

    /// <summary>
    /// Counts this probe and signals stopping before returning healthy storage.
    /// </summary>
    /// <param name="cancellationToken">The caller token, unused by the immediate test probe.</param>
    /// <returns>True for healthy storage while the host begins stopping.</returns>
    public Task<bool> CheckAsync(CancellationToken cancellationToken)
    {
        Calls++;
        lifetime.StopApplication();
        return Task.FromResult(true);
    }
}
