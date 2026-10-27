using CmsEventProcessor.Application.Processing.Options;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CmsEventProcessor.Infrastructure.Persistence;

/// <summary>
/// Disables provider/connection retries and sensitive diagnostics for request-owned SQL transactions.
/// </summary>
internal static class SqlConfiguration
{
    /// <summary>
    /// Configures SQL with bounded commands and no implicit replay or connection retry.
    /// </summary>
    /// <param name="builder">The read or write context options builder.</param>
    /// <param name="connectionString">The private deployment/test connection string.</param>
    /// <param name="processing">The total request budget used to bound individual SQL commands.</param>
    /// <exception cref="ArgumentException">The connection string is invalid.</exception>
    internal static void Configure(DbContextOptionsBuilder builder, string connectionString, ProcessingOptions processing)
    {
        var connection = new SqlConnectionStringBuilder(connectionString)
        {
            ConnectRetryCount = 0,
            PoolBlockingPeriod = PoolBlockingPeriod.NeverBlock
        };
        builder.UseSqlServer(connection.ConnectionString, sql =>
        {
            sql.CommandTimeout((int)Math.Ceiling(processing.Timeout.TotalSeconds));
            sql.ExecutionStrategy(dependencies => new NonRetryingExecutionStrategy(dependencies));
        });
        builder.EnableSensitiveDataLogging(false);
        builder.EnableDetailedErrors(false);
    }

    /// <summary>
    /// Validates a connection without opening it or echoing its secret contents.
    /// </summary>
    /// <param name="value">The deployment connection string to inspect.</param>
    /// <returns>True when a server and database are explicitly configured.</returns>
    internal static bool IsValidConnection(string value)
    {
        try
        {
            var connection = new SqlConnectionStringBuilder(value);
            return !string.IsNullOrWhiteSpace(connection.DataSource) && !string.IsNullOrWhiteSpace(connection.InitialCatalog);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
