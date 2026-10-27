using Microsoft.Extensions.Hosting;

namespace CmsEventProcessor.UnitTests.Operations;

/// <summary>
/// Provides a controllable stopping token for controller-level shutdown readiness checks without stopping a real host.
/// </summary>
internal sealed class TestHostLifetime : IHostApplicationLifetime, IDisposable
{
    private readonly CancellationTokenSource stopping = new();
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => stopping.Token;
    public CancellationToken ApplicationStopped => CancellationToken.None;

    /// <summary>
    /// Signals application stopping while preserving the test's ability to invoke liveness.
    /// </summary>
    public void StopApplication() => stopping.Cancel();

    /// <summary>
    /// Releases only the test token source.
    /// </summary>
    public void Dispose() => stopping.Dispose();
}
