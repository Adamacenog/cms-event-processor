namespace CmsEventProcessor.Application.Operations.Options;

/// <summary>
/// Defines bounded readiness and cooperative shutdown settings, independently of source event timestamps.
/// </summary>
internal sealed class OperationsOptions
{
    public const string SectionName = "Operations";
    public TimeSpan ReadinessTimeout { get; set; } = TimeSpan.FromSeconds(3);
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
