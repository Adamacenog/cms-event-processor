using CmsEventProcessor.Application.Identity.Models;

namespace CmsEventProcessor.Application.Identity.Contracts;

/// <summary>
/// Verifies credentials within an explicit trust boundary and returns only safe identity information.
/// </summary>
public interface IAccountAuthenticator
{
    /// <summary>
    /// Authenticates an identity without exposing storage details to the requesting adapter.
    /// </summary>
    /// <param name="scope">The allowed CMS or consumer trust boundary; unknown values fail closed.</param>
    /// <param name="username">The supplied username, compared ordinally and case-sensitively.</param>
    /// <param name="password">The supplied password, used only for verification.</param>
    /// <returns>The verified identity, or null when credentials or scope are not accepted.</returns>
    /// <exception cref="System.ArgumentNullException">The supplied password is null.</exception>
    AccountIdentity? Authenticate(AccountScope scope, string username, string password);
}
