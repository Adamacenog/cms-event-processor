using System.Net;
using CmsEventProcessor.Api.Operations;
using CmsEventProcessor.Api.Operations.Options;
using CmsEventProcessor.Application.Observability;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CmsEventProcessor.UnitTests.Operations;

/// <summary>
/// Verifies exporter privacy/configuration bounds, peer partitioning, and bounded metric outcomes without opening network services.
/// </summary>
public sealed class OperationalOptionsTests
{
    /// <summary>
    /// Export destinations permit trusted TLS or local collectors while rejecting credentials, queries, fragments, and remote plain HTTP.
    /// </summary>
    /// <param name="endpoint">The proposed collector URI.</param>
    /// <param name="valid">Whether the endpoint satisfies the explicit export boundary.</param>
    [Theory]
    [InlineData("http://127.0.0.1:4318/", true)]
    [InlineData("https://collector.example.test/otel/", true)]
    [InlineData("http://collector.example.test/", false)]
    [InlineData("https://user:secret@collector.example.test/", false)]
    [InlineData("https://collector.example.test/?token=secret", false)]
    [InlineData("https://collector.example.test/#fragment", false)]
    [InlineData("not-a-uri", false)]
    public void ExportDestinationIsExplicitAndBounded(string endpoint, bool valid) => Assert.Equal(valid, OperationalRegistration.IsValidEndpoint(endpoint));

    /// <summary>
    /// IPv4 and its mapped IPv6 representation share the same peer budget, while a different actual peer has an independent budget.
    /// </summary>
    /// <returns>A task completing after admission/rejection and limiter disposal.</returns>
    [Fact]
    public async Task PeerLimitsNormalizeAddressesAndDoNotShareDifferentPeers()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["AuthenticationRateLimit:PermitLimit"] = "1" }).Build());
        services.AddLogging();
        services.AddOperationalReadiness();
        await using var provider = services.BuildServiceProvider();
        await using var limiter = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter!;
        var request = new DefaultHttpContext { RequestServices = provider };
        request.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");
        using var first = await limiter.AcquireAsync(request);
        Assert.True(first.IsAcquired);
        request.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:192.0.2.1");
        using var same = await limiter.AcquireAsync(request);
        Assert.False(same.IsAcquired);
        request.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.2");
        using var different = await limiter.AcquireAsync(request);
        Assert.True(different.IsAcquired);
    }

    /// <summary>
    /// Arbitrary/numeric/internal-invalid outcomes cannot become unbounded metric labels.
    /// </summary>
    [Fact]
    public void OutcomesNeverAcceptArbitrarySourceValues()
    {
        Assert.Equal("Failed", OperationTelemetry.SafeOutcome("CONFIDENTIAL_SOURCE_VALUE"));
        Assert.Equal("Failed", OperationTelemetry.SafeOutcome("0"));
        Assert.Equal("Succeeded", OperationTelemetry.SafeOutcome("Succeeded"));
        Assert.Equal("Replayed", OperationTelemetry.SafeOutcome("Replayed"));
    }

    /// <summary>
    /// An exhausted peer is admitted again after the configured fixed window rather than remaining permanently locked out.
    /// </summary>
    /// <returns>A task completing after bounded real limiter replenishment and resource disposal.</returns>
    [Fact]
    public async Task AuthenticationWindowReplenishes()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["AuthenticationRateLimit:PermitLimit"] = "1", ["AuthenticationRateLimit:Window"] = "00:00:00.1" }).Build());
        services.AddLogging();
        services.AddOperationalReadiness();
        await using var provider = services.BuildServiceProvider();
        await using var limiter = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter!;
        var context = new DefaultHttpContext { RequestServices = provider };
        using (var first = await limiter.AcquireAsync(context)) Assert.True(first.IsAcquired);
        using (var rejected = await limiter.AcquireAsync(context)) Assert.False(rejected.IsAcquired);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (true)
        {
            using var next = await limiter.AcquireAsync(context, cancellationToken: budget.Token);
            if (next.IsAcquired) break;
            await Task.Delay(10, budget.Token);
        }
    }
}
