namespace CmsEventProcessor.Infrastructure.Persistence.Options;

/// <summary>
/// Configures separately selectable read/write connections without claiming physical replica or database privilege isolation.
/// </summary>
internal sealed class PersistenceOptions
{
    public const string SectionName = "Persistence";
    public string WriteConnectionString { get; set; } = string.Empty;
    public string ReadConnectionString { get; set; } = string.Empty;
}
