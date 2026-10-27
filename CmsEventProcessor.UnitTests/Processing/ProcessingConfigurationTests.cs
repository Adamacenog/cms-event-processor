using CmsEventProcessor.Application.Processing.Options;
using CmsEventProcessor.Infrastructure.Persistence;
using CmsEventProcessor.Infrastructure.Persistence.Contexts;
using CmsEventProcessor.Infrastructure.Persistence.Options;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CmsEventProcessor.UnitTests.Processing;

/// <summary>
/// Rejects invalid deadlines and connection shapes without opening storage or exposing connection secrets.
/// </summary>
public sealed class ProcessingConfigurationTests
{
    /// <summary>
    /// Zero, negative, oversized, and internally inconsistent budgets fail options validation.
    /// </summary>
    /// <param name="timeoutSeconds">The overall attempt deadline to validate.</param>
    /// <param name="lockSeconds">The requested lock wait within that deadline.</param>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(301, 1)]
    [InlineData(1, 2)]
    [InlineData(30, -1)]
    public void InvalidProcessingBudgetsAreRejected(int timeoutSeconds, int lockSeconds)
    {
        using var services = CreateServices(new Dictionary<string, string?>
        {
            ["Processing:Timeout"] = TimeSpan.FromSeconds(timeoutSeconds).ToString(),
            ["Processing:LockWait"] = TimeSpan.FromSeconds(lockSeconds).ToString()
        });
        Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<ProcessingOptions>>().Value);
    }

    /// <summary>
    /// Malformed or incomplete connection configuration fails with safe diagnostics containing no configured secret.
    /// </summary>
    [Fact]
    public void InvalidStorageConfigurationDoesNotEchoSecrets()
    {
        const string secret = "CONFIDENTIAL_CONNECTION_SENTINEL";
        using var services = CreateServices(new Dictionary<string, string?>
        {
            ["Persistence:WriteConnectionString"] = "Server=localhost;Password=" + secret,
            ["Persistence:ReadConnectionString"] = "malformed-" + secret
        });
        var exception = Assert.Throws<OptionsValidationException>(() =>
            services.GetRequiredService<IOptions<PersistenceOptions>>().Value);
        Assert.DoesNotContain(secret, exception.Message);
    }

    /// <summary>
    /// Provider and connection options disable implicit retries and cached pool failures without connecting to SQL.
    /// </summary>
    [Fact]
    public void SqlConfigurationDisablesImplicitReplayAndPoolFailureCaching()
    {
        var builder = new DbContextOptionsBuilder<WriteContext>();
        SqlConfiguration.Configure(builder, "Server=127.0.0.1;Database=Unused;Integrated Security=true;ConnectRetryCount=5",
            new ProcessingOptions());
        using var context = new WriteContext(builder.Options);
        var connection = new SqlConnectionStringBuilder(context.Database.GetConnectionString());
        Assert.Equal(0, connection.ConnectRetryCount);
        Assert.Equal(PoolBlockingPeriod.NeverBlock, connection.PoolBlockingPeriod);
        Assert.False(context.Database.CreateExecutionStrategy().RetriesOnFailure);
    }

    /// <summary>
    /// Registers production validation against only the explicitly supplied test configuration.
    /// </summary>
    /// <param name="values">The isolated configuration keys whose validation is under test.</param>
    /// <returns>A disposable provider; no SQL connection is opened.</returns>
    private static ServiceProvider CreateServices(Dictionary<string, string?> values)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        services.AddSqlPersistence();
        return services.BuildServiceProvider();
    }
}
