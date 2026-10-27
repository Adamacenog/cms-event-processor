namespace CmsEventProcessor.Api.Operations.Options;

/// <summary>
/// Enables explicit OTLP export of only sanitized custom metrics/traces; export is disabled by default.
/// </summary>
internal sealed class TelemetryOptions
{
    public const string SectionName = "Observability";
    public bool ExportEnabled { get; set; }
    public string Endpoint { get; set; } = "http://127.0.0.1:4318/";
}
