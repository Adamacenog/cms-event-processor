namespace CmsEventProcessor.Api.Operations.Options;

/// <summary>
/// Bounds pre-authentication attempts per actual socket peer in this process without trusting forwarded headers or queuing requests.
/// </summary>
internal sealed class AuthenticationRateOptions
{
    public const string SectionName = "AuthenticationRateLimit";
    public int PermitLimit { get; set; } = 600;
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
}
