namespace CmsEventProcessor.Application.Identity.Models;

/// <summary>
/// Identifies the configured trust boundary and permissions of an account.
/// </summary>
public enum AccountKind
{
    /// <summary>
    /// The default value is intentionally rejected by startup validation.
    /// </summary>
    Unknown,
    /// <summary>
    /// Organization identity restricted to event submission rather than consumer access.
    /// </summary>
    Cms,
    /// <summary>
    /// Consumer identity restricted to published entities without a local disable.
    /// </summary>
    Reader,
    /// <summary>
    /// Consumer administrator whose permissions do not include CMS event submission.
    /// </summary>
    Admin
}
