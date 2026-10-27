using System.Net;
using System.Text.Json;
using CmsEventProcessor.IntegrationTests.Sql;
using Microsoft.EntityFrameworkCore;

namespace CmsEventProcessor.IntegrationTests.Acceptance;

/// <summary>
/// Runs the actual checked-in Postman walkthrough against a published native API and a migrated disposable SQL catalog.
/// </summary>
/// <param name="fixture">The dedicated SQL server creating and deleting only this test's generated catalog.</param>
[Collection("SQL")]
[Trait("Category", "Acceptance")]
public sealed class PostmanAcceptanceTests(SqlServerFixture fixture)
{
    /// <summary>
    /// Fresh provisioning/migration and all collection assertions succeed over real HTTP; final hard deletion, safe logs, and private-only credentials are verified.
    /// </summary>
    /// <returns>A task completing after the native host, collection, isolated database, and ephemeral secrets are cleaned up.</returns>
    [Fact]
    public async Task PublishedApplicationPassesTheReviewerWalkthrough()
    {
        await using var database = await fixture.CreateAsync();
        await using (var empty = await database.WriterAsync()) await empty.Database.EnsureDeletedAsync();
        await using var api = await PublishedApi.StartAsync(database.ConnectionString);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { BaseAddress = api.Address };
        using (var unauthorized = await client.GetAsync("/api/entities")) Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        var values = new Dictionary<string, string> { ["baseUrl"] = api.Address.ToString().TrimEnd('/') };
        foreach (var (kind, account) in api.Accounts)
        {
            var prefix = kind == "Cms" ? "cms" : kind.ToLowerInvariant();
            values[prefix + "Username"] = account.Username;
            values[prefix + "Password"] = account.Password;
        }
        var output = await AcceptanceProcess.RunAsync("node", [Path.Combine(SqlScenarios.RepositoryRoot, "scripts", "acceptance", "run-newman.cjs")],
            SqlScenarios.RepositoryRoot, input: JsonSerializer.Serialize(values));
        using var result = JsonDocument.Parse(output);
        Assert.Equal(29, result.RootElement.GetProperty("requests").GetInt32());
        Assert.Equal(69, result.RootElement.GetProperty("assertions").GetInt32());
        Assert.Equal(0, result.RootElement.GetProperty("failures").GetInt32());
        await using var observer = await database.WriterAsync();
        Assert.Empty(await observer.Snapshots.ToListAsync());
        Assert.True(await observer.Deletions.AnyAsync());
        Assert.True(await observer.Replays.AnyAsync());
        Assert.Single(await observer.Database.GetAppliedMigrationsAsync());
        Assert.False(api.Logs.Contains("postman-", StringComparison.Ordinal), "Native logs exposed synthetic identifiers.");
        Assert.False(api.Logs.Contains("Synthetic unpublished revision", StringComparison.Ordinal), "Native logs exposed synthetic payloads.");
        foreach (var account in api.Accounts.Values)
        {
            Assert.False(api.Logs.Contains(account.Password, StringComparison.Ordinal), "A generated password appeared in native host logs.");
            Assert.False(output.Contains(account.Password, StringComparison.Ordinal), "A generated password appeared in the safe runner summary.");
        }
    }
}
