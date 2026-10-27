using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace CmsEventProcessor.IntegrationTests.Sql;

/// <summary>
/// Starts the pinned SQL Server container or uses an explicitly configured disposable-test server.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    internal const string Image = "mcr.microsoft.com/mssql/server:2022-CU27-ubuntu-22.04";
    private MsSqlContainer? container;
    private string connection = string.Empty;

    /// <summary>
    /// Obtains a test server without reading application credentials or silently skipping unavailable SQL tests.
    /// </summary>
    /// <returns>A task completing when SQL Server is reachable through the test connection.</returns>
    /// <exception cref="InvalidOperationException">Docker/container startup is unavailable.</exception>
    /// <exception cref="System.Data.Common.DbException">The SQL test server cannot be reached.</exception>
    /// <exception cref="OperationCanceledException">The container startup deadline expires.</exception>
    public async Task InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable("CMS_TEST_SQL_CONNECTION");
        if (!string.IsNullOrWhiteSpace(external))
        {
            connection = external;
        }
        else
        {
            container = new MsSqlBuilder(Image).WithPassword($"T!est{Guid.NewGuid():N}9a").Build();
            using var startup = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await container.StartAsync(startup.Token);
            connection = container.GetConnectionString();
        }
        await using var versionConnection = new SqlConnection(connection);
        await versionConnection.OpenAsync();
        await using var versionCommand = versionConnection.CreateCommand();
        versionCommand.CommandText = "SELECT CONVERT(varchar(128), SERVERPROPERTY('ProductVersion'));";
        var version = (string)(await versionCommand.ExecuteScalarAsync())!;
        var evidence = Path.Combine(SqlScenarios.RepositoryRoot, "artifacts", "test-results");
        Directory.CreateDirectory(evidence);
        await File.WriteAllTextAsync(Path.Combine(evidence, "sql-server-version.txt"),
            $"Source: {(container is null ? "dedicated external server" : Image)}{Environment.NewLine}ProductVersion: {version}{Environment.NewLine}");
    }

    /// <summary>
    /// Creates a uniquely named owned database; an external test server must allow database creation/deletion.
    /// </summary>
    /// <returns>A migrated test database that deletes only its own generated catalog during disposal.</returns>
    /// <exception cref="System.Data.Common.DbException">The dedicated database cannot be created or migrated.</exception>
    internal async Task<SqlTestDatabase> CreateAsync()
    {
        var builder = new SqlConnectionStringBuilder(connection)
        {
            InitialCatalog = "cms_test_" + Guid.NewGuid().ToString("N"),
            Pooling = false,
            ConnectRetryCount = 0
        };
        return await SqlTestDatabase.CreateAsync(builder.ConnectionString);
    }

    /// <summary>
    /// Removes the test-owned container; an external server is never stopped or reconfigured.
    /// </summary>
    /// <returns>A task completing after container cleanup.</returns>
    public async Task DisposeAsync()
    {
        if (container is not null)
        {
            await container.DisposeAsync();
        }
    }
}
