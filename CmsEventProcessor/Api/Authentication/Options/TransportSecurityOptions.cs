namespace CmsEventProcessor.Api.Authentication.Options;

/// <summary>
/// Configures the HTTP adapter's transport exception independently of credential storage.
/// </summary>
internal sealed class TransportSecurityOptions
{
    public const string SectionName = "Authentication";

    /// <summary>
    /// Only affects actual loopback socket peers; forwarded headers cannot enable HTTP access.
    /// </summary>
    public bool AllowInsecureLoopbackHttp { get; set; }
}
