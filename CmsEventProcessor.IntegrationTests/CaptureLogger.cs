using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CmsEventProcessor.IntegrationTests;

/// <summary>
/// Collects all test-host log messages so redaction assertions include framework diagnostics.
/// </summary>
internal sealed class CaptureLogger : ILoggerProvider
{
    public ConcurrentQueue<string> Messages { get; } = new();

    /// <summary>
    /// Creates a category logger writing to the shared test buffer.
    /// </summary>
    /// <param name="categoryName">The framework logging category; all categories share one captured buffer.</param>
    /// <returns>A logger that captures formatted messages and exception text for privacy assertions.</returns>
    public ILogger CreateLogger(string categoryName) => new CaptureLogSink(Messages);

    /// <summary>
    /// Completes provider disposal; the in-memory buffer owns no external resources.
    /// </summary>
    public void Dispose() { }
}
