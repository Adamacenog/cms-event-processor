using CmsEventProcessor.Infrastructure.Identity.Models;

namespace CmsEventProcessor.Infrastructure.Identity.Options;

/// <summary>
/// Holds credential definitions loaded once for the shared identity implementation.
/// </summary>
internal sealed class AccountSettings
{
    public const string SectionName = "Authentication";
    public List<AccountDefinition> Accounts { get; set; } = [];
}
