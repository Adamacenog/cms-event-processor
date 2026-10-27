using System.Threading.RateLimiting;
using CmsEventProcessor.Api.ErrorHandling;
using CmsEventProcessor.Api.Operations.Options;
using CmsEventProcessor.Application.Observability;
using CmsEventProcessor.Application.Operations.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace CmsEventProcessor.Api.Operations;

/// <summary>
/// Registers bounded operations, pre-authentication throttling, and explicitly exported sanitized instrumentation.
/// </summary>
internal static class OperationalRegistration
{
    /// <summary>
    /// Configures validated budgets and custom-only telemetry with no automatic request, SQL, exception, or log export.
    /// </summary>
    /// <param name="services">The host service collection.</param>
    /// <returns>The same service collection.</returns>
    internal static IServiceCollection AddOperationalReadiness(this IServiceCollection services)
    {
        services.AddOptions<OperationsOptions>().BindConfiguration(OperationsOptions.SectionName)
            .Validate(value => value.ReadinessTimeout > TimeSpan.Zero && value.ReadinessTimeout <= TimeSpan.FromSeconds(30) &&
                value.ShutdownTimeout > TimeSpan.Zero && value.ShutdownTimeout <= TimeSpan.FromMinutes(2), "Configure bounded positive readiness and shutdown budgets.")
            .ValidateOnStart();
        services.AddOptions<AuthenticationRateOptions>().BindConfiguration(AuthenticationRateOptions.SectionName)
            .Validate(value => value.PermitLimit is >= 1 and <= 100_000 && value.Window > TimeSpan.Zero && value.Window <= TimeSpan.FromHours(1),
                "Configure a positive bounded authentication attempt limit and window.").ValidateOnStart();
        services.AddOptions<TelemetryOptions>().BindConfiguration(TelemetryOptions.SectionName)
            .Validate(value => IsValidEndpoint(value.Endpoint), "Configure an absolute HTTPS or loopback HTTP OTLP endpoint without user information, query, or fragment.")
            .ValidateOnStart();
        services.AddOptions<HostOptions>().Configure<IOptions<OperationsOptions>>((host, options) => host.ShutdownTimeout = options.Value.ShutdownTimeout);
        services.TryAddSingleton<OperationTelemetry>();
        services.AddRateLimiter(options =>
        {
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var settings = context.RequestServices.GetRequiredService<IOptions<AuthenticationRateOptions>>().Value;
                var peer = context.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(peer, _ => new FixedWindowRateLimiterOptions
                { PermitLimit = settings.PermitLimit, Window = settings.Window, QueueLimit = 0, AutoReplenishment = true });
            });
            options.RejectionStatusCode = 429;
            options.OnRejected = async (context, _) =>
            {
                var response = context.HttpContext.Response;
                response.StatusCode = 429;
                var wait = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry) ? retry : TimeSpan.FromSeconds(1);
                response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var problem = new ProblemDetails { Status = 429, Title = "Too many requests." };
                problem.Extensions["errorCode"] = "rate_limit_exceeded";
                await ProblemResponses.WriteAsync(context.HttpContext, problem);
            };
        });
        var resource = ResourceBuilder.CreateEmpty().AddService("cms-event-processor", autoGenerateServiceInstanceId: false);
        services.AddOpenTelemetry()
            .WithTracing(builder =>
            {
                builder.SetResourceBuilder(resource).AddSource(OperationTelemetry.SourceName).SetSampler(new AlwaysOnSampler());
            })
            .WithMetrics(builder =>
            {
                builder.SetResourceBuilder(resource).AddMeter(OperationTelemetry.SourceName);
            });
        services.ConfigureOpenTelemetryTracerProvider((provider, builder) =>
        {
            var export = provider.GetRequiredService<IOptions<TelemetryOptions>>().Value;
            if (export.ExportEnabled) builder.AddProcessor(new BatchActivityExportProcessor(new OtlpTraceExporter(ExporterOptions(export.Endpoint, "traces")),
                exporterTimeoutMilliseconds: 3000));
        });
        services.ConfigureOpenTelemetryMeterProvider((provider, builder) =>
        {
            var export = provider.GetRequiredService<IOptions<TelemetryOptions>>().Value;
            if (export.ExportEnabled) builder.AddReader(new PeriodicExportingMetricReader(new OtlpMetricExporter(ExporterOptions(export.Endpoint, "metrics")),
                exportIntervalMilliseconds: 10_000, exportTimeoutMilliseconds: 3000));
        });
        return services;
    }

    /// <summary>
    /// Restricts exporter destinations without printing deployment endpoint values during validation.
    /// </summary>
    /// <param name="value">The proposed OTLP base URI.</param>
    /// <returns>True for HTTPS or loopback HTTP with no embedded credentials, query, or fragment.</returns>
    internal static bool IsValidEndpoint(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback) && string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    /// <summary>
    /// Appends the fixed OTLP signal path to the explicitly configured collector base URI.
    /// </summary>
    /// <param name="endpoint">The validated collector base URI.</param>
    /// <param name="signal">The fixed traces or metrics signal.</param>
    /// <returns>The bounded explicit HTTP/protobuf exporter configuration.</returns>
    private static OtlpExporterOptions ExporterOptions(string endpoint, string signal) => new()
    {
        Endpoint = new Uri(endpoint.TrimEnd('/') + "/v1/" + signal),
        Protocol = OtlpExportProtocol.HttpProtobuf,
        TimeoutMilliseconds = 3000
    };
}
