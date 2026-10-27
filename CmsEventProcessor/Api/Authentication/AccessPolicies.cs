namespace CmsEventProcessor.Api.Authentication;

/// <summary>
/// Provides shared scheme, policy, and claim names for authorization boundaries.
/// </summary>
internal static class AccessPolicies
{
    public const string CmsScheme = "CmsBasic";
    public const string ConsumerScheme = "ConsumerBasic";
    public const string Cms = "Cms";
    public const string Consumer = "Consumer";
    public const string Admin = "Admin";
    public const string KindClaim = "account_kind";
}
