using System.Diagnostics;
using System.Text.Json;
using CmsEventProcessor.IntegrationTests.Entities;
using CmsEventProcessor.IntegrationTests.Sql;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using static CmsEventProcessor.IntegrationTests.Webhook.WebhookScenarios;

namespace CmsEventProcessor.IntegrationTests.Operations;

/// <summary>
/// Verifies current schema/read-write readiness and caller-owned recovery using actual isolated SQL databases.
/// </summary>
/// <param name="fixture">The SQL instance creating one migrated owned catalog per case.</param>
[Collection("SQL")]
[Trait("Category", "Sql")]
public sealed class ReadinessSqlTests(SqlServerFixture fixture)
{
    /// <summary>
    /// Correct schema reports ready, and a rejected domain batch does not change storage readiness or process liveness.
    /// </summary>
    /// <returns>A task completing after healthy probes, a real conflict, and healthy follow-up probes.</returns>
    [Fact]
    public async Task DomainConflictDoesNotMakeTheServiceUnready()
    {
        await using var database = await fixture.CreateAsync();
        await using var factory = ConsumerScenarios.Factory(database);
        using var admin = Client(factory, "admin");
        using var cms = Client(factory);
        using (var ready = await admin.GetAsync("/health/ready")) await AssertHealthAsync(ready, 200, "ready");
        using (var first = await PostAsync(cms)) Assert.Equal(200, (int)first.StatusCode);
        using (var conflict = await PostAsync(cms, ValidBody.Replace("CONFIDENTIAL_WEBHOOK_CONTENT", "other")))
            await ProblemAsync(conflict, 409, factory);
        using var final = await admin.GetAsync("/health/ready");
        await AssertHealthAsync(final, 200, "ready");
    }

    /// <summary>
    /// Missing history or an actually missing table makes readiness unavailable without performing migrations or copying dependency errors.
    /// </summary>
    /// <param name="missingHistory">Whether to remove migration history rather than a required table.</param>
    /// <returns>A task completing after schema failure, minimal health JSON, and no implicit repair are verified.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadinessVerifiesHistoryAndActualSchemaWithoutRepair(bool missingHistory)
    {
        await using var database = await fixture.CreateAsync();
        await using (var writer = await database.WriterAsync())
            await writer.Database.ExecuteSqlRawAsync(missingHistory ? "DELETE FROM [__EFMigrationsHistory];" : "DROP TABLE [Snapshots];");
        await using var factory = ConsumerScenarios.Factory(database);
        using var admin = Client(factory, "admin");
        using var response = await admin.GetAsync("/health/ready");
        await AssertHealthAsync(response, 503, "unavailable");
        using var live = await admin.GetAsync("/health/live");
        await AssertHealthAsync(live, 200, "live");
        await using var observer = await database.WriterAsync();
        if (missingHistory) Assert.Empty(await observer.Database.GetAppliedMigrationsAsync());
        else await Assert.ThrowsAsync<SqlException>(() => observer.Snapshots.CountAsync());
    }

    /// <summary>
    /// Readiness depends on both configured connection paths and bounds unavailable probes without exposing private connection details.
    /// </summary>
    /// <param name="readFailure">Whether the independent read connection, rather than writer, is unreachable.</param>
    /// <returns>A task completing after bounded minimal health failure and log privacy are checked.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EitherUnavailableConnectionFailsReadinessWithinBudget(bool readFailure)
    {
        await using var database = await fixture.CreateAsync();
        await using var factory = ConsumerScenarios.Factory(database);
        var bad = new SqlConnectionStringBuilder(database.ConnectionString) { DataSource = "127.0.0.1,1", ConnectTimeout = 1 };
        factory.Settings[readFailure ? "Persistence:ReadConnectionString" : "Persistence:WriteConnectionString"] = bad.ConnectionString;
        factory.Settings["Operations:ReadinessTimeout"] = "00:00:01";
        using var admin = Client(factory, "admin");
        var elapsed = Stopwatch.StartNew();
        using var response = await admin.GetAsync("/health/ready");
        await AssertHealthAsync(response, 503, "unavailable");
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(8));
        Assert.DoesNotContain(bad.Password, string.Join('\n', factory.Logs.Messages));
    }

    /// <summary>
    /// An actual owned database outage makes readiness/ingestion unavailable; restoring it admits the caller's same-key retry without restart or background work.
    /// </summary>
    /// <returns>A task completing after offline/online recovery, retry durability, and minimal liveness/readiness checks.</returns>
    [Fact]
    public async Task DatabaseOutageRecoversForCallerRetryWithoutRestart()
    {
        await using var database = await fixture.CreateAsync();
        await using var factory = ConsumerScenarios.Factory(database);
        factory.Settings["Operations:ReadinessTimeout"] = "00:00:01";
        factory.Settings["Processing:Timeout"] = "00:00:02";
        factory.Settings["Processing:LockWait"] = "00:00:00.1";
        using var admin = Client(factory, "admin");
        using var cms = Client(factory);
        var connection = new SqlConnectionStringBuilder(database.ConnectionString);
        var name = connection.InitialCatalog;
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^cms_test_[0-9a-f]{32}$")) throw new InvalidOperationException("Only the test-owned database may be taken offline.");
        connection.InitialCatalog = "master";
        await using var server = new SqlConnection(connection.ConnectionString);
        await server.OpenAsync();
        await using var command = server.CreateCommand();
        try
        {
            command.CommandText = $"ALTER DATABASE [{name}] SET OFFLINE WITH ROLLBACK IMMEDIATE;";
            await command.ExecuteNonQueryAsync();
            using (var notReady = await admin.GetAsync("/health/ready")) await AssertHealthAsync(notReady, 503, "unavailable");
            using (var live = await admin.GetAsync("/health/live")) await AssertHealthAsync(live, 200, "live");
            using var failed = await PostAsync(cms, key: "outage-retry-key");
            await ProblemAsync(failed, 503, factory);
        }
        finally
        {
            command.CommandText = $"ALTER DATABASE [{name}] SET ONLINE;";
            await command.ExecuteNonQueryAsync();
        }
        using (var ready = await admin.GetAsync("/health/ready")) await AssertHealthAsync(ready, 200, "ready");
        using var retry = await PostAsync(cms, key: "outage-retry-key");
        Assert.Equal(200, (int)retry.StatusCode);
        await using var observer = await database.WriterAsync();
        Assert.Single(await observer.Snapshots.ToListAsync());
        Assert.Single(await observer.Replays.ToListAsync());
    }

    /// <summary>
    /// Checks the health contract's exact minimal JSON and no-store policy rather than treating unavailable health as a detailed error page.
    /// </summary>
    /// <param name="response">The live/readiness HTTP response.</param>
    /// <param name="status">The expected HTTP status.</param>
    /// <param name="value">The single fixed status value.</param>
    /// <returns>A task completing after exact shape, status, content type, and caching are checked.</returns>
    private static async Task AssertHealthAsync(HttpResponseMessage response, int status, string value)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Single(body.RootElement.EnumerateObject());
        Assert.Equal(value, body.RootElement.GetProperty("status").GetString());
    }
}
