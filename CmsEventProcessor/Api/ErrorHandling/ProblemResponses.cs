using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace CmsEventProcessor.Api.ErrorHandling;

/// <summary>
/// Writes sanitized JSON problems even when the client requests an unsupported error media type.
/// </summary>
internal static class ProblemResponses
{
    /// <summary>
    /// Preserves the failure status and trace correlation without exposing exception or request content.
    /// </summary>
    /// <param name="context">The request context providing the response status, output stream, and trace identifier.</param>
    /// <param name="problem">The sanitized problem to serialize, or null to derive one from the response status.</param>
    /// <returns>A task that completes when the JSON Problem Details response has been written.</returns>
    /// <exception cref="System.OperationCanceledException">The request is canceled while its response is being written.</exception>
    /// <exception cref="System.IO.IOException">The response stream cannot be written.</exception>
    public static Task WriteAsync(HttpContext context, ProblemDetails? problem = null)
    {
        problem ??= new ProblemDetails
        {
            Status = context.Response.StatusCode,
            Title = ReasonPhrases.GetReasonPhrase(context.Response.StatusCode)
        };
        problem.Type ??= "about:blank";
        problem.Extensions["traceId"] = context.TraceIdentifier;
        return context.Response.WriteAsJsonAsync((object)problem, options: (JsonSerializerOptions?)null, contentType: "application/problem+json",
            cancellationToken: context.RequestAborted);
    }
}
