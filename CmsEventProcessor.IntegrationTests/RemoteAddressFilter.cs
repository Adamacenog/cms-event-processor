using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace CmsEventProcessor.IntegrationTests;

/// <summary>
/// Sets the socket peer before security middleware to test loopback-only HTTP access.
/// </summary>
/// <param name="address">A literal IP address to simulate as the socket peer, independently of forwarded headers.</param>
internal sealed class RemoteAddressFilter(string address) : IStartupFilter
{
    /// <summary>
    /// Prepends peer-address simulation while preserving the production middleware order.
    /// </summary>
    /// <param name="next">The remaining application-pipeline configuration to invoke after registering peer simulation.</param>
    /// <returns>A configuration delegate that prepends socket-peer simulation and preserves the remaining middleware order.</returns>
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, continuation) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(address);
            return continuation(context);
        });
        next(app);
    };
}
