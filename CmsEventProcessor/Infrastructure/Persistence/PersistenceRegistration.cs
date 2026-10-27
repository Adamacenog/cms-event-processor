using CmsEventProcessor.Application.Processing.Contracts;
using CmsEventProcessor.Application.Processing.Options;
using CmsEventProcessor.Application.Processing.Services;
using CmsEventProcessor.Infrastructure.Persistence.Contexts;
using CmsEventProcessor.Infrastructure.Persistence.Options;
using CmsEventProcessor.Infrastructure.Persistence.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CmsEventProcessor.Infrastructure.Persistence;

/// <summary>
/// Connects application mutations to bounded SQL contexts without running migrations during web startup.
/// </summary>
internal static class PersistenceRegistration
{
    /// <summary>
    /// Registers validated connections, non-retrying factories, retention time, and mutation use cases.
    /// </summary>
    /// <param name="services">The composition root's service collection.</param>
    /// <returns>The same collection for further registration.</returns>
    internal static IServiceCollection AddSqlPersistence(this IServiceCollection services)
    {
        services.AddOptions<ProcessingOptions>().BindConfiguration(ProcessingOptions.SectionName)
            .Validate(options => options.Timeout > TimeSpan.Zero && options.Timeout <= TimeSpan.FromMinutes(5) &&
                options.LockWait >= TimeSpan.Zero && options.LockWait <= options.Timeout,
                "Processing timeout must be positive and at most five minutes; lock wait must fit that budget.").ValidateOnStart();
        services.AddOptions<PersistenceOptions>().BindConfiguration(PersistenceOptions.SectionName)
            .Validate(options => SqlConfiguration.IsValidConnection(options.WriteConnectionString) &&
                SqlConfiguration.IsValidConnection(options.ReadConnectionString),
                "Configure valid read and write SQL connections with an explicit server and database.").ValidateOnStart();
        services.AddSingleton(TimeProvider.System);
        services.AddDbContextFactory<WriteContext>((provider, builder) =>
        {
            var storage = provider.GetRequiredService<IOptions<PersistenceOptions>>().Value;
            SqlConfiguration.Configure(builder, storage.WriteConnectionString,
                provider.GetRequiredService<IOptions<ProcessingOptions>>().Value);
        });
        services.AddDbContextFactory<ReadContext>((provider, builder) =>
        {
            var storage = provider.GetRequiredService<IOptions<PersistenceOptions>>().Value;
            SqlConfiguration.Configure(builder, storage.ReadConnectionString,
                provider.GetRequiredService<IOptions<ProcessingOptions>>().Value);
        });
        // SQL exception messages can include confidential values even without sensitive-data logging.
        services.AddLogging(logging => logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.None));
        services.AddSingleton<IMutationTransactionFactory, SqlMutationTransactionFactory>();
        services.AddSingleton<BatchProcessor>();
        services.AddSingleton<EntityDisabler>();
        return services;
    }
}
