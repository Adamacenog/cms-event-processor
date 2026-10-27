using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using CmsEventProcessor.Application.Identity.Contracts;
using CmsEventProcessor.Application.Identity.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace CmsEventProcessor.Api.Authentication.Handlers;

/// <summary>
/// Parses bounded Basic credentials and creates claims only from configured account permissions.
/// </summary>
/// <param name="options">The framework monitor supplying the selected authentication scheme options.</param>
/// <param name="logger">The framework logger factory; credential values are never included in authentication failures.</param>
/// <param name="encoder">The URL encoder required by the authentication-handler base class.</param>
/// <param name="accounts">The shared authentication contract, returning verified identities without hashes.</param>
internal sealed class BasicAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IAccountAuthenticator accounts) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Rejects malformed headers and verifies credentials within the selected authentication scheme.
    /// </summary>
    /// <returns>A completed task containing an authenticated ticket, no result for an absent header, or a uniform credential failure.</returns>
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var headers = Request.Headers.Authorization;
        if (headers.Count == 0)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (headers.Count != 1 || headers[0] is not { Length: <= 4096 } header ||
            !AuthenticationHeaderValue.TryParse(header, out var value) ||
            !string.Equals(value.Scheme, "Basic", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(value.Parameter) || value.Parameter.Any(char.IsWhiteSpace))
        {
            return InvalidCredentials();
        }

        string decoded;
        try
        {
            decoded = StrictUtf8.GetString(Convert.FromBase64String(value.Parameter));
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            return InvalidCredentials();
        }

        var separator = decoded.IndexOf(':');
        if (separator <= 0 || separator == decoded.Length - 1 || decoded.Any(char.IsControl))
        {
            return InvalidCredentials();
        }

        var scope = Scheme.Name switch
        {
            AccessPolicies.CmsScheme => AccountScope.Cms,
            AccessPolicies.ConsumerScheme => AccountScope.Consumer,
            _ => AccountScope.Unknown
        };
        var account = accounts.Authenticate(scope, decoded[..separator], decoded[(separator + 1)..]);
        if (account is null)
        {
            return InvalidCredentials();
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, account.Username),
            new Claim(ClaimTypes.Name, account.Username),
            new Claim(AccessPolicies.KindClaim, account.Kind.ToString()),
            new Claim(ClaimTypes.Role, account.Kind.ToString())
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }

    /// <summary>
    /// Issues a Basic challenge identifying the CMS or consumer authentication realm.
    /// </summary>
    /// <param name="properties">Authentication properties supplied by the framework challenge pipeline.</param>
    /// <returns>A completed task after setting HTTP 401 and the appropriate Basic realm challenge.</returns>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        var realm = Scheme.Name == AccessPolicies.CmsScheme ? "cms" : "consumers";
        Response.Headers.WWWAuthenticate = $"Basic realm=\"{realm}\", charset=\"UTF-8\"";
        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates a uniform failure without echoing credentials or disclosing account existence.
    /// </summary>
    /// <returns>A completed task containing a generic authentication failure with no credential or account details.</returns>
    private static Task<AuthenticateResult> InvalidCredentials() =>
        Task.FromResult(AuthenticateResult.Fail("Invalid credentials."));
}
