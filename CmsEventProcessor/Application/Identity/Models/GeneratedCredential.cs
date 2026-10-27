namespace CmsEventProcessor.Application.Identity.Models;

/// <summary>
/// Carries a provisioned identity and its new password for explicit, one-time operator disclosure.
/// </summary>
/// <param name="Account">The identity and permissions without exposing its stored hash.</param>
/// <param name="Password">The newly generated password to display once to the operator.</param>
public sealed record GeneratedCredential(AccountIdentity Account, string Password);
