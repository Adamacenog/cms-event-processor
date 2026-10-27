using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CmsEventProcessor.IntegrationTests;

/// <summary>
/// Captures formatted messages and exception text to expose accidental confidential logging in tests.
/// </summary>
/// <param name="messages">The shared, thread-safe buffer receiving captured test-host log entries.</param>
internal sealed class CaptureLogSink(ConcurrentQueue<string> messages) : ILogger
{
    /// <summary>
    /// Accepts scopes without allocating resources because assertions inspect messages only.
    /// </summary>
    /// <typeparam name="TState">The type of logging scope state supplied by the framework.</typeparam>
    /// <param name="state">Framework scope metadata, accepted without allocating a scope object.</param>
    /// <returns>Null because the capture logger does not create disposable scopes.</returns>
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <summary>
    /// Captures every level allowed by production privacy filters without adding sink-specific exclusions.
    /// </summary>
    /// <param name="logLevel">The requested log level; every level is enabled for privacy checks.</param>
    /// <returns>Always true so filtering cannot conceal sensitive output from assertions.</returns>
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <summary>
    /// Stores both message and exception content for later privacy assertions.
    /// </summary>
    /// <typeparam name="TState">The type of structured logging state.</typeparam>
    /// <param name="logLevel">The framework log level; this sink captures all levels.</param>
    /// <param name="eventId">The event identifier supplied by the framework; assertions inspect formatted content.</param>
    /// <param name="state">The structured state passed to the message formatter.</param>
    /// <param name="exception">The optional exception whose text is also captured to detect accidental disclosure.</param>
    /// <param name="formatter">The framework formatter that renders the structured message.</param>
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception) + exception);
}
