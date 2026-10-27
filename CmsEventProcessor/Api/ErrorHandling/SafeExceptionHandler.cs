using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace CmsEventProcessor.Api.ErrorHandling;

/// <summary>
/// Produces generic failure responses and logs correlation without confidential exception content.
/// </summary>
/// <param name="logger">The logger receiving only exception type and trace correlation, never the exception object or message.</param>
internal sealed partial class SafeExceptionHandler(
    ILogger<SafeExceptionHandler> logger) : IExceptionHandler
{
    /// <summary>
    /// Converts an unhandled exception into a correlated HTTP 500 Problem Details response.
    /// </summary>
    /// <param name="context">The request whose response is replaced with a sanitized HTTP 500 problem.</param>
    /// <param name="exception">The failure to classify by type without disclosing its message or contents.</param>
    /// <param name="cancellationToken">The framework handling token; response writing observes the request context cancellation token.</param>
    /// <returns>A value task returning true after the safe error response is written.</returns>
    /// <exception cref="System.OperationCanceledException">The request is canceled while its response is being written.</exception>
    /// <exception cref="System.IO.IOException">The response stream cannot be written.</exception>
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        // Exception messages may contain confidential input; log only type and correlation.
        UnhandledFailure(logger, exception.GetType().Name, context.TraceIdentifier);
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await ProblemResponses.WriteAsync(context,
            new ProblemDetails { Status = 500, Title = "An unexpected error occurred." });
        return true;
    }

    /// <summary>
    /// Records only exception type and trace ID, deliberately excluding message and exception object.
    /// </summary>
    /// <param name="logger">The logger receiving the structured failure event.</param>
    /// <param name="exceptionType">The safe exception type name, excluding its message and stack trace.</param>
    /// <param name="traceId">The correlation identifier for the failed request.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled failure {ExceptionType}; trace {TraceId}")]
    private static partial void UnhandledFailure(ILogger logger, string exceptionType, string traceId);
}
