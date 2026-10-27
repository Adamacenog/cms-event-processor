using CmsEventProcessor.Api.Authentication;
using CmsEventProcessor.Api.Entities.Dtos;
using CmsEventProcessor.Api.Entities.Mappings;
using CmsEventProcessor.Api.Entities.Validation;
using CmsEventProcessor.Api.ErrorHandling;
using CmsEventProcessor.Application.Entities.Contracts;
using CmsEventProcessor.Application.Entities.Exceptions;
using CmsEventProcessor.Application.Identity.Models;
using CmsEventProcessor.Application.Processing.Models;
using CmsEventProcessor.Application.Processing.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CmsEventProcessor.Api.Entities.Controllers;

/// <summary>
/// Exposes role-aware current snapshots and an administrator-only local override without content-update operations.
/// </summary>
/// <param name="reader">The application read contract applying visibility in bounded database queries.</param>
/// <param name="disabler">The application mutation service sharing ingestion's coordination and transaction protocol.</param>
[ApiController]
[Route("api/entities")]
[Authorize(Policy = AccessPolicies.Consumer)]
internal sealed class EntitiesController(IEntityReader reader, EntityDisabler disabler) : ControllerBase
{
    /// <summary>
    /// Returns a bounded visible page with a continuation only when a further matching row exists.
    /// </summary>
    /// <param name="cancellationToken">The HTTP request-abort token.</param>
    /// <returns>A task completing after page JSON or safe Problem Details are written.</returns>
    /// <exception cref="OperationCanceledException">The caller cancels the read or response.</exception>
    /// <exception cref="IOException">The response transport fails.</exception>
    [HttpGet]
    [ProducesResponseType(typeof(EntityPageResponse), 200, "application/json")]
    [ProducesResponseType(typeof(ValidationProblemDetails), 400, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), 401, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), 503, "application/problem+json")]
    public async Task ListAsync(CancellationToken cancellationToken)
    {
        var includeHidden = User.IsInRole(nameof(AccountKind.Admin));
        var errors = EntityInputValidator.ValidatePage(Request.Query, includeHidden, out var size, out var afterId);
        if (errors.Count > 0) { await InvalidAsync(errors); return; }
        try
        {
            var page = await reader.ListAsync(includeHidden, size, afterId, cancellationToken);
            await Response.WriteAsJsonAsync(new EntityPageResponse(page.Items.Select(EntityResponseMapper.Map).ToArray(),
                page.NextId is null ? null : EntityCursor.Encode(page.NextId, includeHidden)), cancellationToken);
        }
        catch (EntityReadException exception) { await UnavailableAsync(exception.TimedOut ? "read_timeout" : "storage_unavailable"); }
    }

    /// <summary>
    /// Returns a single snapshot under the same visibility policy as listing, using indistinguishable missing/hidden 404s.
    /// </summary>
    /// <param name="id">The framework route value, resolved from the original encoded segment when available.</param>
    /// <param name="cancellationToken">The HTTP request-abort token.</param>
    /// <returns>A task completing after entity JSON or a safe failure response.</returns>
    /// <exception cref="OperationCanceledException">The caller cancels the read or response.</exception>
    /// <exception cref="IOException">The response transport fails.</exception>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(EntityResponse), 200, "application/json")]
    [ProducesResponseType(typeof(ValidationProblemDetails), 400, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), 401, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), 404, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), 503, "application/problem+json")]
    public async Task FindAsync(string id, CancellationToken cancellationToken)
    {
        id = EntityInputValidator.ReadRouteId(Request, id);
        if (!EntityInputValidator.IsValidId(id)) { await InvalidAsync(new() { ["id"] = ["Supply a valid source identifier."] }); return; }
        try
        {
            var entity = await reader.FindAsync(id, User.IsInRole(nameof(AccountKind.Admin)), cancellationToken);
            if (entity is null) { Response.StatusCode = 404; await ProblemResponses.WriteAsync(HttpContext); return; }
            await Response.WriteAsJsonAsync(EntityResponseMapper.Map(entity), cancellationToken);
        }
        catch (EntityReadException exception) { await UnavailableAsync(exception.TimedOut ? "read_timeout" : "storage_unavailable"); }
    }

    /// <summary>
    /// Applies only the persistent local override and waits for commit before returning 204.
    /// </summary>
    /// <param name="id">The framework route value, resolved from the original encoded segment when available.</param>
    /// <param name="cancellationToken">The HTTP request-abort token shared with the mutation attempt.</param>
    /// <returns>A task completing after acknowledged disabling or safe validation/access/storage failure.</returns>
    /// <exception cref="OperationCanceledException">The caller cancels body checking or the mutation.</exception>
    /// <exception cref="IOException">The request or response transport fails.</exception>
    [HttpPut("{id}/disable")]
    [Authorize(Policy = AccessPolicies.Admin)]
    [ProducesResponseType(204)]
    [ProducesResponseType(typeof(ValidationProblemDetails), 400, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), 401, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), 403, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), 404, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), 503, "application/problem+json")]
    public async Task DisableAsync(string id, CancellationToken cancellationToken)
    {
        id = EntityInputValidator.ReadRouteId(Request, id, disable: true);
        if (!EntityInputValidator.IsValidId(id)) { await InvalidAsync(new() { ["id"] = ["Supply a valid source identifier."] }); return; }
        if (Request.ContentLength > 0 || await Request.Body.ReadAsync(new byte[1], cancellationToken) > 0)
        { await InvalidAsync(new() { ["body"] = ["This operation does not accept a request body."] }); return; }
        var result = await disabler.DisableAsync(id, cancellationToken);
        switch (result)
        {
            case MutationStatus.Succeeded: Response.StatusCode = 204; return;
            case MutationStatus.NotFound: Response.StatusCode = 404; await ProblemResponses.WriteAsync(HttpContext); return;
            default:
                await UnavailableAsync(result == MutationStatus.TimedOut ? "processing_timeout" :
                    result == MutationStatus.CommitUnknown ? "commit_unknown" : "storage_unavailable");
                return;
        }
    }

    /// <summary>
    /// Writes fixed-field validation problems without serializing source identifiers, cursor values, or request content.
    /// </summary>
    /// <param name="errors">The fixed safe error paths/messages.</param>
    /// <returns>A task completing after the correlated 400 problem is written.</returns>
    /// <exception cref="OperationCanceledException">The caller cancels response writing.</exception>
    /// <exception cref="IOException">The response transport fails.</exception>
    private Task InvalidAsync(Dictionary<string, string[]> errors)
    {
        Response.StatusCode = 400;
        return ProblemResponses.WriteAsync(HttpContext, new ValidationProblemDetails(errors) { Status = 400, Title = "Request validation failed." });
    }

    /// <summary>
    /// Writes a retryable safe read/mutation classification without claiming rollback after an uncertain commit.
    /// </summary>
    /// <param name="errorCode">The fixed operation classification.</param>
    /// <returns>A task completing after a correlated 503 problem and retry hint are written.</returns>
    /// <exception cref="OperationCanceledException">The caller cancels response writing.</exception>
    /// <exception cref="IOException">The response transport fails.</exception>
    private Task UnavailableAsync(string errorCode)
    {
        Response.StatusCode = 503;
        Response.Headers.RetryAfter = "1";
        var problem = new ProblemDetails { Status = 503, Title = "The operation could not be completed or confirmed." };
        problem.Extensions["errorCode"] = errorCode;
        return ProblemResponses.WriteAsync(HttpContext, problem);
    }
}
