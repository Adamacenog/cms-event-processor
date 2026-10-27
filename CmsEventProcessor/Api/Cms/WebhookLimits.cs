namespace CmsEventProcessor.Api.Cms;

/// <summary>
/// Defines the documented ingestion bounds shared by body reading, validation, and OpenAPI metadata.
/// </summary>
internal static class WebhookLimits
{
    public const int BodyBytes = 1_048_576;
    public const int Events = 100;
    public const int JsonDepth = 32;
}
