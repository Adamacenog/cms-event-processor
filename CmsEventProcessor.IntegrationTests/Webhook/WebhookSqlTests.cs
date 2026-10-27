using System.Text;
using System.Text.Json;
using CmsEventProcessor.Infrastructure.Persistence.Contexts;
using CmsEventProcessor.IntegrationTests.Sql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CmsEventProcessor.IntegrationTests.Webhook.WebhookScenarios;

namespace CmsEventProcessor.IntegrationTests.Webhook;

/// <summary>
/// Verifies production webhook responses against durable SQL state, replay races, and complete-batch rollback.
/// </summary>
/// <param name="fixture">The shared SQL instance creating an isolated migrated database for each case.</param>
[Collection("SQL")]
[Trait("Category", "Sql")]
public sealed class WebhookSqlTests(SqlServerFixture fixture)
{
    /// <summary>
    /// A successful same-ID publish/unpublish batch commits unseen version content before 200, with final metadata and no polling URL.
    /// </summary>
    /// <returns>A task completing after response outcomes, retained unpublished content, and deletion metadata are verified.</returns>
    [Fact]
    public async Task SuccessContainsFinalOutcomesAndImmediatelyDurableState()
    {
        await using var database = await fixture.CreateAsync();
        await using var factory = Factory(database);
        using var client = Client(factory);
        var body = Encoding.UTF8.GetString(SqlScenarios.Body([
            SqlScenarios.Source(), SqlScenarios.Source(version: 2, seconds: 1, type: Domain.Entities.Models.CmsEventType.Unpublish),
            SqlScenarios.Source("deleted", type: Domain.Entities.Models.CmsEventType.Delete)]));
        using var response = await PostAsync(client, body, "successful-key");
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Null(response.Headers.Location);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.NotEqual(Guid.Empty, result.RootElement.GetProperty("batchId").GetGuid());
        Assert.Equal(new[] { "published", "unpublished", "deleted" }, result.RootElement.GetProperty("outcomes").EnumerateArray()
            .Select(value => value.GetProperty("reasonCode").GetString()));
        await using var observer = await database.WriterAsync();
        var snapshot = Assert.Single(await observer.Snapshots.ToListAsync());
        Assert.Equal(2, snapshot.Version);
        Assert.False(snapshot.CmsPublished);
        Assert.Single(await observer.Deletions.ToListAsync());
        Assert.Single(await observer.Replays.ToListAsync());
        Assert.Contains("Committed batch", string.Join('\n', factory.Logs.Messages));
        Assert.DoesNotContain("Synthetic", string.Join('\n', factory.Logs.Messages));
    }

    /// <summary>
    /// A later malformed event prevents the otherwise valid first event from reaching the transaction or reserving a key.
    /// </summary>
    /// <returns>A task completing after safe validation and empty persisted state/replay tables are checked.</returns>
    [Fact]
    public async Task EntireBatchIsValidatedBeforeAnyMutation()
    {
        await using var database = await fixture.CreateAsync();
        await using var factory = Factory(database);
        using var client = Client(factory);
        using var response = await PostAsync(client, ValidBody[..^1] + ",{\"type\":\"delete\",\"id\":null}]", "invalid-later");
        var problem = await ProblemAsync(response, 400, factory);
        Assert.True(problem.GetProperty("errors").TryGetProperty("[1].id", out _));
        Assert.DoesNotContain("Committed batch", string.Join('\n', factory.Logs.Messages));
        await using var observer = await database.WriterAsync();
        Assert.Empty(await observer.Snapshots.ToListAsync());
        Assert.Empty(await observer.Replays.ToListAsync());
    }

    /// <summary>
    /// A valid later version/content conflict rolls back prior SQL writes and returns no tentative successes or reserved key.
    /// </summary>
    /// <returns>A task completing after the 409 event index, rollback, and corrected same-key retry are verified.</returns>
    [Fact]
    public async Task ConflictRollsBackTheWholeBatchAndDoesNotReserveItsKey()
    {
        await using var database = await fixture.CreateAsync();
        await using var factory = Factory(database);
        using var client = Client(factory);
        using var seed = await PostAsync(client);
        Assert.Equal(200, (int)seed.StatusCode);
        var conflict = ValidBody.Replace("CONFIDENTIAL_WEBHOOK_CONTENT", "different");
        var first = ValidBody.Replace("article-001", "temporary");
        using var response = await PostAsync(client, first[..^1] + "," + conflict[1..], "conflicted-key");
        var problem = await ProblemAsync(response, 409, factory, "version_payload_conflict");
        Assert.Equal(1, problem.GetProperty("eventIndex").GetInt32());
        Assert.False(problem.TryGetProperty("outcomes", out _));
        await using (var observer = await database.WriterAsync())
        {
            Assert.Single(await observer.Snapshots.ToListAsync());
            Assert.Empty(await observer.Replays.ToListAsync());
        }
        using var retry = await PostAsync(client, first, "conflicted-key");
        Assert.Equal(200, (int)retry.StatusCode);
    }

    /// <summary>
    /// Identical keyed bytes replay across HTTP host restart, while changed whitespace conflicts and a second CMS identity has its own scope.
    /// </summary>
    /// <returns>A task completing after stable response bytes, safe key conflict, and principal isolation are checked.</returns>
    [Fact]
    public async Task ReplayIsExactDurableAndScopedToAuthenticatedCms()
    {
        await using var database = await fixture.CreateAsync();
        string original;
        await using (var first = Factory(database))
        {
            using var client = Client(first);
            using var response = await PostAsync(client, key: "replay-key");
            Assert.Equal(200, (int)response.StatusCode);
            original = await response.Content.ReadAsStringAsync();
        }
        await using var restarted = Factory(database);
        restarted.Settings["Authentication:Accounts:3:Username"] = "cms-test0002";
        restarted.Settings["Authentication:Accounts:3:Kind"] = "Cms";
        restarted.Settings["Authentication:Accounts:3:PasswordHash"] = TestAccounts.Configuration["Authentication:Accounts:0:PasswordHash"];
        using var retryClient = Client(restarted);
        using var replay = await PostAsync(retryClient, key: "replay-key");
        Assert.Equal(original, await replay.Content.ReadAsStringAsync());
        using var mismatch = await PostAsync(retryClient, ValidBody + " ", "replay-key");
        await ProblemAsync(mismatch, 409, restarted, "idempotency_key_conflict");
        AuthenticationHttpTests.SetCredentials(retryClient, "cms-test0002", TestAccounts.CmsPassword);
        using var separate = await PostAsync(retryClient, ValidBody + " ", "replay-key");
        Assert.Equal(200, (int)separate.StatusCode);
        await using var observer = await database.WriterAsync();
        Assert.Equal(2, await observer.Replays.CountAsync());
        Assert.Single(await observer.Snapshots.ToListAsync());
    }

    /// <summary>
    /// Concurrent identical keyed requests return one original response; concurrent different bodies have exactly one key winner.
    /// </summary>
    /// <param name="differentBody">Whether the racing request changes exact bytes.</param>
    /// <returns>A task completing after both HTTP outcomes and single durable replay record are checked.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentKeyedRequestsHaveOneCommittedResult(bool differentBody)
    {
        await using var database = await fixture.CreateAsync();
        await using var factory = Factory(database);
        using var client = Client(factory);
        var requests = await Task.WhenAll(PostAsync(client, key: "race-key"), PostAsync(client, ValidBody + (differentBody ? " " : ""), "race-key"));
        try
        {
            if (differentBody)
            {
                Assert.Equal(new[] { 200, 409 }, requests.Select(value => (int)value.StatusCode).Order().ToArray());
                await ProblemAsync(requests.Single(value => (int)value.StatusCode == 409), 409, factory, "idempotency_key_conflict");
            }
            else
            {
                Assert.All(requests, value => Assert.Equal(200, (int)value.StatusCode));
                Assert.Equal(await requests[0].Content.ReadAsStringAsync(), await requests[1].Content.ReadAsStringAsync());
            }
            await using var observer = await database.WriterAsync();
            Assert.Single(await observer.Snapshots.ToListAsync());
            Assert.Single(await observer.Replays.ToListAsync());
        }
        finally
        {
            foreach (var response in requests) response.Dispose();
        }
    }
}
