using System.Diagnostics;
using System.Text;
using CmsEventProcessor.Application.Identity.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using static CmsEventProcessor.IntegrationTests.Webhook.WebhookScenarios;

namespace CmsEventProcessor.IntegrationTests.Operations;

/// <summary>
/// Exercises production health authorization, pre-authentication limits, and actual sanitized OTLP export without SQL.
/// </summary>
public sealed class OperationsHttpTests
{
    /// <summary>
    /// Only administrators reach health checks; liveness remains independent of deliberately unavailable storage.
    /// </summary>
    /// <param name="route">The live or ready route.</param>
    /// <param name="kind">The supplied identity, or missing-credential case.</param>
    /// <param name="status">The expected challenge, forbid, or live success.</param>
    /// <returns>A task completing after authorization and minimal liveness output are verified.</returns>
    [Theory]
    [InlineData("live", "missing", 401)]
    [InlineData("ready", "missing", 401)]
    [InlineData("live", "cms", 401)]
    [InlineData("ready", "cms", 401)]
    [InlineData("live", "reader", 403)]
    [InlineData("ready", "reader", 403)]
    [InlineData("live", "admin", 200)]
    public async Task HealthAccessIsAdministratorOnly(string route, string kind, int status)
    {
        await using var factory = new FoundationFactory { IncludeTestControllers = false };
        using var client = kind == "missing" ? factory.Client() : Client(factory, kind);
        using var response = await client.GetAsync("/health/" + route);
        if (status != 200) await ProblemAsync(response, status, factory);
        else { Assert.Equal("{\"status\":\"live\"}", await response.Content.ReadAsStringAsync()); Assert.Equal("no-store", response.Headers.CacheControl?.ToString()); }
    }

    /// <summary>
    /// Excess attempts are rejected before authentication, do not trust forwarded peer headers, and return safe retryable 429s without queuing.
    /// </summary>
    /// <returns>A task completing after adapter call counts, rejection status, and privacy checks.</returns>
    [Fact]
    public async Task AuthenticationLimitPrecedesVerificationAndIgnoresForwardedHeaders()
    {
        var authenticator = new CountingAuthenticator();
        await using var factory = new FoundationFactory
        {
            Settings = new() { ["AuthenticationRateLimit:PermitLimit"] = "2" },
            ConfigureTestServices = services => services.AddSingleton<IAccountAuthenticator>(authenticator)
        };
        using var client = Client(factory);
        for (var index = 0; index < 2; index++)
        { using var response = await PostAsync(client, "invalid"); await ProblemAsync(response, 401, factory); }
        var calls = authenticator.Calls;
        Assert.True(calls > 0);
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Forwarded-For", "198.51.100.88");
        using var rejected = await PostAsync(client);
        await ProblemAsync(rejected, 429, factory, "rate_limit_exceeded");
        Assert.True(rejected.Headers.RetryAfter?.Delta >= TimeSpan.FromSeconds(1));
        Assert.Equal(calls, authenticator.Calls);
        using var again = await PostAsync(client);
        await ProblemAsync(again, 429, factory);
    }

    /// <summary>
    /// Custom activities/metrics and real exported protobuf omit raw IDs, cursors, credentials, payloads, incoming baggage, and trace state.
    /// </summary>
    /// <returns>A task completing after SDK flush, local packet receipt, and complete custom-tag/privacy assertions.</returns>
    [Fact]
    public async Task ActualOtlpExportContainsOnlySanitizedCustomInstrumentation()
    {
        await using var collector = await TelemetryCollector.StartAsync();
        using var capture = new TelemetryCapture();
        await using var factory = new FoundationFactory
        {
            Settings = new() { ["Observability:ExportEnabled"] = "true", ["Observability:Endpoint"] = collector.Endpoint }
        };
        using var client = Client(factory, "reader");
        client.DefaultRequestHeaders.TryAddWithoutValidation("traceparent", "00-11111111111111111111111111111111-2222222222222222-01");
        client.DefaultRequestHeaders.TryAddWithoutValidation("tracestate", "vendor=CONFIDENTIAL_TRACE_STATE");
        client.DefaultRequestHeaders.TryAddWithoutValidation("baggage", "secret=CONFIDENTIAL_BAGGAGE");
        using (var response = await client.GetAsync("/api/entities/%20CONFIDENTIAL_SOURCE_ID?cursor=CONFIDENTIAL_CURSOR"))
            await ProblemAsync(response, 400, factory);
        Assert.True(factory.Services.GetRequiredService<TracerProvider>().ForceFlush(5000));
        Assert.True(factory.Services.GetRequiredService<MeterProvider>().ForceFlush(5000));
        Assert.NotEmpty(collector.Packets);
        Assert.NotEmpty(capture.Activities);
        Assert.NotEmpty(capture.Measurements);
        var text = string.Join('\n', collector.Packets.Select(Encoding.UTF8.GetString)) + string.Join('\n', capture.Measurements);
        Assert.Contains("cms.http", text);
        foreach (var secret in new[] { "CONFIDENTIAL_SOURCE_ID", "CONFIDENTIAL_CURSOR", "CONFIDENTIAL_TRACE_STATE", "CONFIDENTIAL_BAGGAGE", TestAccounts.ReaderPassword })
            Assert.DoesNotContain(secret, text);
        foreach (var activity in capture.Activities)
        {
            Assert.Null(activity.TraceStateString);
            Assert.Empty(activity.Baggage);
            Assert.NotEqual("11111111111111111111111111111111", activity.TraceId.ToHexString());
            Assert.DoesNotContain("CONFIDENTIAL", string.Join('|', activity.TagObjects));
        }
    }

    /// <summary>
    /// Invalid operational budgets, rate limits, and collector destinations fail web startup with fixed validation messages.
    /// </summary>
    /// <param name="key">The fixed deployment option under test.</param>
    /// <param name="value">The deliberately invalid option value.</param>
    /// <returns>A task completing after failed startup and test-host disposal.</returns>
    [Theory]
    [InlineData("Operations:ReadinessTimeout", "00:00:00")]
    [InlineData("Operations:ShutdownTimeout", "00:00:00")]
    [InlineData("AuthenticationRateLimit:PermitLimit", "0")]
    [InlineData("AuthenticationRateLimit:PermitLimit", "100001")]
    [InlineData("AuthenticationRateLimit:Window", "00:00:00")]
    [InlineData("Observability:Endpoint", "http://collector.example.test/")]
    public async Task InvalidOperationalConfigurationFailsStartup(string key, string value)
    {
        await using var factory = new FoundationFactory { Settings = new() { [key] = value } };
        Assert.Throws<OptionsValidationException>(() => factory.Client());
    }
}
