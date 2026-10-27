using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace CmsEventProcessor.IntegrationTests.Operations;

/// <summary>
/// Receives actual OTLP HTTP/protobuf packets on an ephemeral loopback Kestrel listener without exporting them externally.
/// </summary>
internal sealed class TelemetryCollector : IAsyncDisposable
{
    private readonly WebApplication host;
    public ConcurrentQueue<byte[]> Packets { get; } = new();
    public string Endpoint => host.Urls.Single() + "/";

    /// <summary>
    /// Associates the private in-memory packet sink with the test-owned collector host.
    /// </summary>
    /// <param name="host">The local collector host.</param>
    private TelemetryCollector(WebApplication host) => this.host = host;

    /// <summary>
    /// Starts a local receiver that accepts protobuf exports without persisting their content.
    /// </summary>
    /// <returns>The asynchronously disposable collector.</returns>
    /// <exception cref="IOException">The loopback listener cannot bind.</exception>
    internal static async Task<TelemetryCollector> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = Path.GetTempPath(), EnvironmentName = "Collector" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var collector = new TelemetryCollector(app);
        app.Run(async context =>
        {
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body);
            collector.Packets.Enqueue(body.ToArray());
            context.Response.StatusCode = 200;
        });
        await app.StartAsync();
        return collector;
    }

    /// <summary>
    /// Stops and disposes only this test's local listener.
    /// </summary>
    /// <returns>A value task completing after listener shutdown.</returns>
    public async ValueTask DisposeAsync() { await host.StopAsync(); await host.DisposeAsync(); }
}
