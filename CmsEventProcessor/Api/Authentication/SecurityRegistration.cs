using CmsEventProcessor.Api.Authentication.Handlers;
using CmsEventProcessor.Api.Authentication.Options;
using CmsEventProcessor.Application.Identity.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;

namespace CmsEventProcessor.Api.Authentication;

/// <summary>
/// Registers HTTP transport options, Basic authentication schemes, and deny-by-default access policies.
/// </summary>
internal static class SecurityRegistration
{
    /// <summary>
    /// Configures isolated Basic schemes and explicit CMS, consumer, and administrator policies.
    /// </summary>
    /// <param name="services">The application service collection receiving authentication and authorization registrations.</param>
    /// <returns>The same service collection, allowing further registration calls to be chained.</returns>
    public static IServiceCollection AddConfiguredAuthentication(this IServiceCollection services)
    {
        services.AddOptions<TransportSecurityOptions>()
            .BindConfiguration(TransportSecurityOptions.SectionName)
            .ValidateOnStart();
        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = AccessPolicies.ConsumerScheme;
            options.DefaultChallengeScheme = AccessPolicies.ConsumerScheme;
        })
            .AddScheme<AuthenticationSchemeOptions, BasicAuthenticationHandler>(AccessPolicies.CmsScheme, null)
            .AddScheme<AuthenticationSchemeOptions, BasicAuthenticationHandler>(AccessPolicies.ConsumerScheme, null);

        services.AddAuthorization(options =>
        {
            var consumer = new AuthorizationPolicyBuilder(AccessPolicies.ConsumerScheme)
                .RequireAuthenticatedUser().RequireRole(nameof(AccountKind.Reader), nameof(AccountKind.Admin)).Build();
            options.DefaultPolicy = consumer;
            options.FallbackPolicy = consumer;
            options.AddPolicy(AccessPolicies.Consumer, consumer);
            options.AddPolicy(AccessPolicies.Cms, new AuthorizationPolicyBuilder(AccessPolicies.CmsScheme)
                .RequireAuthenticatedUser().RequireClaim(AccessPolicies.KindClaim, nameof(AccountKind.Cms)).Build());
            options.AddPolicy(AccessPolicies.Admin, new AuthorizationPolicyBuilder(AccessPolicies.ConsumerScheme)
                .RequireAuthenticatedUser().RequireRole(nameof(AccountKind.Admin)).Build());
        });
        return services;
    }
}
