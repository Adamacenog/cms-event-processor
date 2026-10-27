namespace CmsEventProcessor.Application.Identity.Models;

/// <summary>
/// Selects the trust boundary in which credentials are allowed to authenticate.
/// </summary>
public enum AccountScope
{
    /// <summary>
    /// An unspecified boundary fails closed even when supplied credentials are otherwise valid.
    /// </summary>
    Unknown,
    /// <summary>
    /// Accepts only CMS organization identities.
    /// </summary>
    Cms,
    /// <summary>
    /// Accepts reader and administrator consumer identities, excluding CMS identities.
    /// </summary>
    Consumer
}
