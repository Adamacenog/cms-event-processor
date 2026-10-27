using CmsEventProcessor.Application.Identity.Contracts;
using CmsEventProcessor.Infrastructure.Identity.Options;
using CmsEventProcessor.Infrastructure.Identity.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CmsEventProcessor.Infrastructure.Identity;

/// <summary>
/// Registers the shared identity implementation without depending on HTTP or API classes.
/// </summary>
public static class IdentityRegistration
{
    /// <summary>
    /// Adds credential validation, startup snapshots, password verification, and explicit provisioning.
    /// </summary>
    /// <param name="services">The composition root's service collection.</param>
    /// <returns>The same collection for further service registration.</returns>
    public static IServiceCollection AddIdentityInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IValidateOptions<AccountSettings>, AccountSettingsValidator>();
        services.AddOptions<AccountSettings>().BindConfiguration(AccountSettings.SectionName).ValidateOnStart();
        services.AddSingleton<PasswordHashService>();
        services.AddSingleton<IAccountAuthenticator, AccountCatalog>();
        services.AddSingleton<IAccountProvisioner, CredentialFile>();
        return services;
    }
}
