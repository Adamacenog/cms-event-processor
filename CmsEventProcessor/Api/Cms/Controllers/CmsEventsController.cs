using CmsEventProcessor.Api.Authentication;
using CmsEventProcessor.Api.Cms.Dtos;
using CmsEventProcessor.Api.Cms.Services;
using CmsEventProcessor.Api.ErrorHandling;
using CmsEventProcessor.Application.Processing.Models;
using CmsEventProcessor.Application.Processing.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CmsEventProcessor.Api.Cms.Controllers;

/// <summary>
/// Accepts CMS-only batches and reports final results after the request-owned SQL attempt completes.
/// </summary>
/// <param name="reader">The bounded JSON reader and complete-batch validator.</param>
/// <param name="processor">The application service that commits state and replay metadata together.</param>
/// <param name="logger">The logger receiving HTTP correlation, safe classifications, and error counts without source values.</param>
[ApiController]
[Route("cms/events")]
[Authorize(Policy = AccessPolicies.Cms)]
internal sealed class CmsEventsController(WebhookRequestReader reader, BatchProcessor processor, ILogger<CmsEventsController> logger) : ControllerBase
{
    /// <summary>
    /// Validates the complete submitted array and waits for commit or a safely classified failure.
    /// </summary>
    /// <param name="cancellationToken">The HTTP request-abort token propagated through reading and SQL processing.</param>
    /// <returns>A task completing after committed metadata or sanitized Problem Details are written.</returns>
    /// <exception cref="OperationCanceledException">The caller cancels reading or processing.</exception>
    /// <exception cref="IOException">The request or response transport fails.</exception>
    [HttpPost]
    [ProducesResponseType(typeof(CmsBatchResponse), 200, "application/json")]
    [ProducesResponseType(typeof(ValidationProblemDetails), 400, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), 401, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), 409, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), 413, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), 415, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), 500, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), 503, "application/problem+json")]
    public async Task PostAsync(CancellationToken cancellationToken)
    {
        var input = await reader.ReadAsync(Request, User.Identity!.Name!, cancellationToken);
        if (input.Request is null)
        {
            logger.LogWarning("Webhook rejected with status {Status}; trace {TraceId}; invalid field count {ErrorCount}.",
                input.StatusCode, HttpContext.TraceIdentifier, input.Errors?.Count ?? 0);
            Response.StatusCode = input.StatusCode;
            ProblemDetails problem = input.Errors is not null ?
                new ValidationProblemDetails(input.Errors) { Title = "Request validation failed." } :
                new ProblemDetails { Title = input.StatusCode == 413 ? "Request body is too large." : "Use application/json encoded as UTF-8 without compression." };
            problem.Status = input.StatusCode;
            await ProblemResponses.WriteAsync(HttpContext, problem);
            return;
        }
        var result = await processor.ProcessAsync(input.Request, cancellationToken);
        if (result.Status == MutationStatus.Succeeded)
        {
            var success = result.Success!;
            await Response.WriteAsJsonAsync(new CmsBatchResponse(success.BatchId,
                success.Outcomes.Select(value => new CmsEventResponse(value.Index, value.Id, value.Outcome, value.ReasonCode)).ToArray()),
                cancellationToken);
            return;
        }
        Response.StatusCode = result.Status is MutationStatus.VersionConflict or MutationStatus.IdempotencyConflict ? 409 : 503;
        var error = new ProblemDetails
        {
            Status = Response.StatusCode,
            Title = Response.StatusCode == 409 ? "The batch conflicts with existing state." : "Processing could not be confirmed."
        };
        error.Extensions["errorCode"] = result.Status switch
        {
            MutationStatus.VersionConflict => "version_payload_conflict",
            MutationStatus.IdempotencyConflict => "idempotency_key_conflict",
            MutationStatus.TimedOut => "processing_timeout",
            MutationStatus.CommitUnknown => "commit_unknown",
            _ => "storage_unavailable"
        };
        if (result.EventIndex.HasValue)
        {
            error.Extensions["eventIndex"] = result.EventIndex.Value;
        }
        logger.LogWarning("Webhook processing returned {ErrorCode}; trace {TraceId}; event {EventIndex}.",
            error.Extensions["errorCode"], HttpContext.TraceIdentifier, result.EventIndex);
        if (Response.StatusCode == 503)
        {
            Response.Headers.RetryAfter = "1";
        }
        await ProblemResponses.WriteAsync(HttpContext, error);
    }
}
