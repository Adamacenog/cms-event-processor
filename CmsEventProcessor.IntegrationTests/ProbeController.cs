using CmsEventProcessor.Api.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CmsEventProcessor.IntegrationTests;

/// <summary>
/// Provides protected probe actions loaded only by the integration-test host.
/// </summary>
[ApiController]
[Route("_test")]
public sealed class ProbeController : ControllerBase
{
    /// <summary>
    /// Exercises the policy limited to the CMS organization scheme.
    /// </summary>
    /// <returns>An HTTP 200 action result containing the authenticated probe marker.</returns>
    [HttpGet("cms")]
    [Authorize(Policy = AccessPolicies.Cms)]
    public IActionResult Cms() => Ok(new { authenticated = true });

    /// <summary>
    /// Exercises consumer access shared by readers and administrators.
    /// </summary>
    /// <returns>An HTTP 200 action result containing the authenticated probe marker.</returns>
    [HttpGet("consumer")]
    [Authorize(Policy = AccessPolicies.Consumer)]
    public IActionResult Consumer() => Ok(new { authenticated = true });

    /// <summary>
    /// Exercises administrator-only access within the consumer scheme.
    /// </summary>
    /// <returns>An HTTP 200 action result containing the authenticated probe marker.</returns>
    [HttpGet("admin")]
    [Authorize(Policy = AccessPolicies.Admin)]
    public IActionResult Admin() => Ok(new { authenticated = true });

    /// <summary>
    /// Verifies deny-by-default authorization on an action without an explicit policy.
    /// </summary>
    /// <returns>An HTTP 200 action result containing the authenticated probe marker.</returns>
    [HttpGet("fallback")]
    public IActionResult Fallback() => Ok(new { authenticated = true });

    /// <summary>
    /// Throws a confidential sentinel to test sanitized error responses and logs.
    /// </summary>
    /// <returns>This method never returns an action result.</returns>
    /// <exception cref="System.InvalidOperationException">Always thrown with a confidential sentinel to exercise safe exception handling.</exception>
    [HttpGet("failure")]
    [Authorize(Policy = AccessPolicies.Consumer)]
    public IActionResult Failure() => throw new InvalidOperationException("CONFIDENTIAL_EXCEPTION_CONTENT");

    /// <summary>
    /// Exercises model-binding failures without exposing attempted request values.
    /// </summary>
    /// <param name="input">The test-only input accepted after MVC binding and validation succeed.</param>
    /// <returns>An HTTP 200 action result echoing the validated probe input.</returns>
    [HttpPost("validation")]
    [Authorize(Policy = AccessPolicies.Consumer)]
    public IActionResult Validation(ProbeInput input) => Ok(input);
}
