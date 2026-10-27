using CmsEventProcessor.Application.Identity.Contracts;
using CmsEventProcessor.Application.Identity.Models;

namespace CmsEventProcessor.IntegrationTests.Operations;

/// <summary>
/// Counts attempted password verification without retaining or printing supplied credential values.
/// </summary>
internal sealed class CountingAuthenticator : IAccountAuthenticator
{
    private int calls;
    public int Calls => Volatile.Read(ref calls);

    /// <summary>
    /// Records an adapter verification attempt and rejects the synthetic credentials uniformly.
    /// </summary>
    /// <param name="scope">The requested trust boundary.</param>
    /// <param name="username">The unretained synthetic username.</param>
    /// <param name="password">The unretained synthetic password.</param>
    /// <returns>Null for the deliberate negative authentication case.</returns>
    public AccountIdentity? Authenticate(AccountScope scope, string username, string password)
    {
        Interlocked.Increment(ref calls);
        return null;
    }
}
