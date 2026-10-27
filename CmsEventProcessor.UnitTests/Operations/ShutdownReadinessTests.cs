using CmsEventProcessor.Api.Health.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CmsEventProcessor.UnitTests.Operations;

/// <summary>
/// Verifies shutdown state before and during probing without conflating healthy SQL with admission readiness.
/// </summary>
public sealed class ShutdownReadinessTests
{
    /// <summary>
    /// Stopping before or during a probe prevents ready responses, while liveness remains available for a still-serving process.
    /// </summary>
    /// <param name="alreadyStopping">Whether stopping precedes the controller call or occurs inside its probe.</param>
    /// <returns>A task completing after unavailable readiness and independent liveness are checked.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownCannotAdvertiseReady(bool alreadyStopping)
    {
        using var lifetime = new TestHostLifetime();
        var probe = new StoppingReadinessProbe(lifetime);
        if (alreadyStopping) lifetime.StopApplication();
        using var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();
        var controller = new HealthController(probe, lifetime) { ControllerContext = new ControllerContext { HttpContext = context } };
        await controller.ReadyAsync(default);
        Assert.Equal(503, context.Response.StatusCode);
        Assert.Equal(alreadyStopping ? 0 : 1, probe.Calls);
        context.Response.StatusCode = 200;
        context.Response.Body = new MemoryStream();
        await controller.LiveAsync(default);
        Assert.Equal(200, context.Response.StatusCode);
    }
}
