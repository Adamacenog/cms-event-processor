using System.Diagnostics;
using CmsEventProcessor.Application.Observability;
using Microsoft.AspNetCore.Routing;

namespace CmsEventProcessor.Api.Operations.Services;

/// <summary>
/// Wraps the complete HTTP pipeline with sanitized local tracing and route-template metrics, excluding raw URLs and request context.
/// </summary>
/// <param name="next">The remaining pipeline, including exception handling and authorization.</param>
internal sealed class RequestTelemetryMiddleware(RequestDelegate next)
{
    private static readonly HashSet<string> Routes = ["cms/events", "api/entities", "api/entities/{id}", "api/entities/{id}/disable",
        "health/live", "health/ready", "/openapi/{documentName}.json"];

    /// <summary>
    /// Measures final response status after error handling without attaching headers, payloads, IDs, usernames, or exception details.
    /// </summary>
    /// <param name="context">The active request.</param>
    /// <param name="telemetry">The custom fixed instrument owner.</param>
    /// <returns>A task completing after the response pipeline and safe metric recording.</returns>
    /// <exception cref="OperationCanceledException">A caller cancellation escapes the downstream pipeline.</exception>
    /// <exception cref="IOException">The downstream transport fails.</exception>
    public async Task InvokeAsync(HttpContext context, OperationTelemetry telemetry)
    {
        var started = Stopwatch.GetTimestamp();
        using var observation = telemetry.Begin("http", context.RequestAborted);
        var failed = false;
        try { await next(context); }
        catch { failed = true; throw; }
        finally
        {
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
            if (!Routes.Contains(route)) route = "unmatched";
            var method = context.Request.Method is "GET" or "POST" or "PUT" ? context.Request.Method : "OTHER";
            var status = context.RequestAborted.IsCancellationRequested ? 499 : failed ? 500 : context.Response.StatusCode;
            if (status is < 100 or > 599) status = 500;
            Activity.Current?.SetTag("http.route", route);
            Activity.Current?.SetTag("http.request.method", method);
            Activity.Current?.SetTag("http.response.status_code", status);
            observation.SetOutcome(status < 400 ? "Succeeded" : "Failed");
            telemetry.RecordHttp(route, method, status, Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
    }
}
