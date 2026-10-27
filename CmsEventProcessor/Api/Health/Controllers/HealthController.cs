using CmsEventProcessor.Api.Authentication;
using CmsEventProcessor.Api.Health.Dtos;
using CmsEventProcessor.Application.Operations.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CmsEventProcessor.Api.Health.Controllers;

/// <summary>
/// Provides administrator-only process liveness and current schema/storage readiness, without disclosing dependencies.
/// </summary>
/// <param name="probe">The bounded current SQL readiness contract.</param>
/// <param name="lifetime">The host lifetime used to stop advertising readiness during shutdown.</param>
[ApiController]
[Route("health")]
[Authorize(Policy = AccessPolicies.Admin)]
internal sealed class HealthController(IReadinessProbe probe, IHostApplicationLifetime lifetime) : ControllerBase
{
    /// <summary>
    /// Reports that the authenticated HTTP process is currently serving requests, independently of database failures.
    /// </summary>
    /// <param name="cancellationToken">The requesting caller token.</param>
    /// <returns>A task completing after minimal live JSON is written.</returns>
    /// <exception cref="OperationCanceledException">The caller cancels writing.</exception>
    /// <exception cref="IOException">The response transport fails.</exception>
    [HttpGet("live")]
    [ProducesResponseType(typeof(HealthResponse), 200, "application/json")]
    [ProducesResponseType(typeof(ProblemDetails), 401, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), 403, "application/problem+json")]
    public Task LiveAsync(CancellationToken cancellationToken) => Response.WriteAsJsonAsync(new HealthResponse("live"), cancellationToken);

    /// <summary>
    /// Returns ready only while the host accepts work and both configured storage paths satisfy the expected schema.
    /// </summary>
    /// <param name="cancellationToken">The requesting caller token propagated through the probe.</param>
    /// <returns>A task completing after minimal ready or unavailable JSON is written.</returns>
    /// <exception cref="OperationCanceledException">The caller aborts probing or writing.</exception>
    /// <exception cref="IOException">The response transport fails.</exception>
    [HttpGet("ready")]
    [ProducesResponseType(typeof(HealthResponse), 200, "application/json")]
    [ProducesResponseType(typeof(HealthResponse), 503, "application/json")]
    [ProducesResponseType(typeof(ProblemDetails), 401, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), 403, "application/problem+json")]
    public async Task ReadyAsync(CancellationToken cancellationToken)
    {
        var ready = !lifetime.ApplicationStopping.IsCancellationRequested && await probe.CheckAsync(cancellationToken);
        ready &= !lifetime.ApplicationStopping.IsCancellationRequested;
        Response.StatusCode = ready ? 200 : 503;
        await Response.WriteAsJsonAsync(new HealthResponse(ready ? "ready" : "unavailable"), cancellationToken);
    }
}
