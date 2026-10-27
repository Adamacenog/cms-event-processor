namespace CmsEventProcessor.Application.Processing.Options;

/// <summary>
/// Bounds one mutation attempt across connection, locking, domain evaluation, writes, and commit.
/// </summary>
internal sealed class ProcessingOptions
{
    public const string SectionName = "Processing";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan LockWait { get; set; } = TimeSpan.FromSeconds(5);
}
