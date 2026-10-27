using CmsEventProcessor.Application.Processing.Options;
using CmsEventProcessor.Application.Processing.Services;
using CmsEventProcessor.Infrastructure.Persistence;
using CmsEventProcessor.Infrastructure.Persistence.Contexts;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CmsEventProcessor.IntegrationTests.Sql;

/// <summary>
/// Owns one migrated test catalog and production service registrations with isolated clocks and logs.
/// </summary>
internal sealed class SqlTestDatabase : IAsyncDisposable
{
    public string ConnectionString { get; }
    public TestTimeProvider Clock { get; } = new();
    public CaptureLogger Logs { get; } = new();
    public ServiceProvider Services { get; }
    public BatchProcessor Processor => Services.GetRequiredService<BatchProcessor>();
    public EntityDisabler Disabler => Services.GetRequiredService<EntityDisabler>();

    /// <summary>
    /// Creates service registrations scoped to an already selected, uniquely named test catalog.
    /// </summary>
    /// <param name="connectionString">The private connection for this test-owned database.</param>
    private SqlTestDatabase(string connectionString)
    {
        ConnectionString = connectionString;
        Services = CreateServices();
    }

    /// <summary>
    /// Applies checked-in migrations explicitly, without relying on application startup or EnsureCreated.
    /// </summary>
    /// <param name="connectionString">The generated test catalog connection.</param>
    /// <returns>The migrated test resource.</returns>
    /// <exception cref="System.Data.Common.DbException">Migration or database creation fails.</exception>
    internal static async Task<SqlTestDatabase> CreateAsync(string connectionString)
    {
        var database = new SqlTestDatabase(connectionString);
        await using var context = await database.WriterAsync();
        await context.Database.MigrateAsync();
        return database;
    }

    /// <summary>
    /// Builds an independent application instance for restart, failure injection, or alternate timeout checks.
    /// </summary>
    /// <param name="options">Optional per-instance processing budgets.</param>
    /// <param name="connectionOverride">An optional unreachable test connection for failure-budget checks.</param>
    /// <param name="interceptors">Optional SQL/commit fault probes attached after initial migration.</param>
    /// <returns>A disposable provider sharing only this test database, clock, and captured logs.</returns>
    internal ServiceProvider CreateServices(ProcessingOptions? options = null, string? connectionOverride = null, params IInterceptor[] interceptors)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Persistence:WriteConnectionString"] = connectionOverride ?? ConnectionString,
                ["Persistence:ReadConnectionString"] = connectionOverride ?? ConnectionString
            }).Build());
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
        services.AddSqlPersistence();
        services.AddSingleton<TimeProvider>(Clock);
        if (options is not null)
        {
            services.Configure<ProcessingOptions>(value =>
            {
                value.Timeout = options.Timeout;
                value.LockWait = options.LockWait;
            });
        }
        if (interceptors.Length > 0)
        {
            services.AddDbContextFactory<WriteContext>(builder => builder.AddInterceptors(interceptors));
        }
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Opens a fresh production writer context for observing committed database state.
    /// </summary>
    /// <returns>An independently disposable writer context.</returns>
    internal Task<WriteContext> WriterAsync() => Services.GetRequiredService<IDbContextFactory<WriteContext>>().CreateDbContextAsync();

    /// <summary>
    /// Removes only this test's generated catalog after disposing application instances.
    /// </summary>
    /// <returns>A value task completing after owned database deletion and provider disposal.</returns>
    /// <exception cref="InvalidOperationException">The catalog name is not a generated test name.</exception>
    /// <exception cref="System.Data.Common.DbException">The owned database cannot be deleted.</exception>
    public async ValueTask DisposeAsync()
    {
        var name = new SqlConnectionStringBuilder(ConnectionString).InitialCatalog;
        if (!name.StartsWith("cms_test_", StringComparison.Ordinal) || name.Length != 41)
        {
            throw new InvalidOperationException("Refusing to delete a database not owned by this test.");
        }
        await using (var context = await WriterAsync())
        {
            await context.Database.EnsureDeletedAsync();
        }
        await Services.DisposeAsync();
    }
}
