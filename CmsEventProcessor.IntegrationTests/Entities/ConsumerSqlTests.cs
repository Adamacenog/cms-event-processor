using System.Text.Json;
using CmsEventProcessor.Domain.Entities.Models;
using CmsEventProcessor.IntegrationTests.Sql;
using Microsoft.EntityFrameworkCore;
using static CmsEventProcessor.IntegrationTests.Webhook.WebhookScenarios;

namespace CmsEventProcessor.IntegrationTests.Entities;

/// <summary>
/// Verifies role-filtered current snapshots, binary seek pagination, deletion privacy, and durable administrator overrides through HTTP/SQL.
/// </summary>
/// <param name="fixture">The shared SQL instance supplying one isolated migrated catalog per case.</param>
[Collection("SQL")]
[Trait("Category", "Sql")]
public sealed class ConsumerSqlTests(SqlServerFixture fixture)
{
    /// <summary>
    /// Listing and lookup apply the same publication/override matrix; administrators retain all current content while readers see only visible state.
    /// </summary>
    /// <param name="published">The CMS publication flag.</param>
    /// <param name="disabled">The persistent local override flag.</param>
    /// <param name="role">The authenticated consumer role.</param>
    /// <returns>A task completing after list/lookup shape, visibility, payload preservation, and privacy assertions.</returns>
    [Theory]
    [InlineData(true, false, "reader")]
    [InlineData(true, true, "reader")]
    [InlineData(false, false, "reader")]
    [InlineData(false, true, "reader")]
    [InlineData(true, false, "admin")]
    [InlineData(true, true, "admin")]
    [InlineData(false, false, "admin")]
    [InlineData(false, true, "admin")]
    public async Task ListingAndLookupShareVisibility(bool published, bool disabled, string role)
    {
        await using var database = await fixture.CreateAsync();
        await using var factory = ConsumerScenarios.Factory(database);
        using var cms = Client(factory);
        using var consumer = Client(factory, role);
        await ConsumerScenarios.SendAsync(cms, "CONFIDENTIAL_SOURCE_ID", version: 2, type: published ? CmsEventType.Publish : CmsEventType.Unpublish);
        if (disabled) Assert.Equal(Application.Processing.Models.MutationStatus.Succeeded, await database.Disabler.DisableAsync("CONFIDENTIAL_SOURCE_ID"));
        using var list = await consumer.GetAsync("/api/entities?pageSize=1");
        Assert.Equal(200, (int)list.StatusCode);
        using var page = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var visible = role == "admin" || published && !disabled;
        Assert.Equal(visible ? 1 : 0, page.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, page.RootElement.GetProperty("nextCursor").ValueKind);
        using var lookup = await consumer.GetAsync("/api/entities/CONFIDENTIAL_SOURCE_ID");
        if (visible)
        {
            Assert.Equal(200, (int)lookup.StatusCode);
            using var body = JsonDocument.Parse(await lookup.Content.ReadAsStringAsync());
            Assert.Equal(2, body.RootElement.GetProperty("version").GetInt64());
            Assert.Equal(9007199254740993, body.RootElement.GetProperty("payload").GetProperty("number").GetInt64());
            Assert.Equal(published, body.RootElement.GetProperty("cmsPublished").GetBoolean());
            Assert.Equal(disabled, body.RootElement.GetProperty("adminDisabled").GetBoolean());
            Assert.Equal(6, body.RootElement.EnumerateObject().Count());
        }
        else await ProblemAsync(lookup, 404, factory);
        Assert.Equal("no-store", list.Headers.CacheControl?.ToString());
        var logs = string.Join('\n', factory.Logs.Messages);
        Assert.DoesNotContain("CONFIDENTIAL_SOURCE_ID", logs);
        Assert.DoesNotContain("CONFIDENTIAL_WEBHOOK_CONTENT", logs);
        Assert.DoesNotContain(TestAccounts.ReaderPassword, logs);
    }

    /// <summary>
    /// Binary source-ID seek ordering pages all visible rows once, with filtering before the limit and a single projected TOP query per request.
    /// </summary>
    /// <param name="role">The reader or administrator visibility.</param>
    /// <returns>A task completing after pagination, terminal cursor, and executed SQL shape are verified.</returns>
    [Theory]
    [InlineData("reader")]
    [InlineData("admin")]
    public async Task PagesAreBoundedOrderedAndFilteredInSql(string role)
    {
        await using var database = await fixture.CreateAsync();
        var probe = new ReadProbe();
        await using var factory = ConsumerScenarios.Factory(database, readProbe: probe);
        using var cms = Client(factory);
        using var consumer = Client(factory, role);
        foreach (var id in new[] { "a", "A", "b", "B", "C", "D", "é", "中" })
            await ConsumerScenarios.SendAsync(cms, id, type: id == "B" ? CmsEventType.Unpublish : CmsEventType.Publish);
        await database.Disabler.DisableAsync("C");
        var ids = new List<string>();
        string? cursor = null;
        var requests = 0;
        do
        {
            using var response = await consumer.GetAsync("/api/entities?pageSize=2" + (cursor is null ? "" : "&cursor=" + cursor));
            Assert.Equal(200, (int)response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var items = body.RootElement.GetProperty("items");
            Assert.InRange(items.GetArrayLength(), 1, 2);
            ids.AddRange(items.EnumerateArray().Select(value => value.GetProperty("id").GetString()!));
            cursor = body.RootElement.GetProperty("nextCursor").GetString();
            Assert.True(++requests < 10);
        } while (cursor is not null);
        var expected = role == "admin" ? new[] { "A", "B", "C", "D", "a", "b", "é", "中" } : new[] { "A", "D", "a", "b", "é", "中" };
        Assert.Equal(expected, ids);
        Assert.Equal(requests, probe.Commands.Count);
        Assert.All(probe.Commands, sql =>
        {
            Assert.Contains("TOP(", sql);
            Assert.Contains("ORDER BY", sql);
            Assert.DoesNotContain("RowVersion", sql);
            Assert.DoesNotContain("COUNT(", sql);
            Assert.DoesNotContain("OFFSET", sql);
        });
        Assert.All(probe.IntegerParameters, count => Assert.Equal(3, count));
        if (role == "reader") Assert.All(probe.Commands, sql => { Assert.Contains("[CmsPublished]", sql); Assert.Contains("[AdminDisabled]", sql); });
    }

    /// <summary>
    /// Disabling is durable/idempotent, preserves CMS fields, survives later publication, and ends only with hard deletion and new lifecycle creation.
    /// </summary>
    /// <returns>A task completing after the complete CMS/consumer override lifecycle is checked without sleeps or polling.</returns>
    [Fact]
    public async Task AdministratorOverrideSurvivesRepublishAndEndsWithDeletion()
    {
        await using var database = await fixture.CreateAsync();
        await using var factory = ConsumerScenarios.Factory(database);
        using var cms = Client(factory);
        using var admin = Client(factory, "admin");
        using var reader = Client(factory, "reader");
        await ConsumerScenarios.SendAsync(cms);
        byte[] initialRowVersion;
        using (var disabled = await admin.PutAsync("/api/entities/article-001/disable", null)) Assert.Equal(204, (int)disabled.StatusCode);
        await using (var observer = await database.WriterAsync())
        {
            var row = await observer.Snapshots.SingleAsync();
            initialRowVersion = row.RowVersion.ToArray();
            Assert.True(row.AdminDisabled);
            Assert.Equal(1, row.Version);
            Assert.Equal(SqlScenarios.Origin, row.CmsTimestamp);
        }
        using (var repeat = await admin.PutAsync("/api/entities/article-001/disable", null)) Assert.Equal(204, (int)repeat.StatusCode);
        await using (var observer = await database.WriterAsync()) Assert.Equal(initialRowVersion, (await observer.Snapshots.SingleAsync()).RowVersion);
        await ConsumerScenarios.SendAsync(cms, version: 2, seconds: 1);
        using (var hidden = await reader.GetAsync("/api/entities/article-001")) await ProblemAsync(hidden, 404, factory);
        using (var retained = await admin.GetAsync("/api/entities/article-001"))
        {
            using var content = JsonDocument.Parse(await retained.Content.ReadAsStringAsync());
            Assert.Equal(2, content.RootElement.GetProperty("version").GetInt32());
            Assert.True(content.RootElement.GetProperty("adminDisabled").GetBoolean());
        }
        await ConsumerScenarios.SendAsync(cms, seconds: 2, type: CmsEventType.Delete);
        foreach (var client in new[] { admin, reader })
        {
            using var deleted = await client.GetAsync("/api/entities/article-001");
            await ProblemAsync(deleted, 404, factory);
        }
        using (var absent = await admin.PutAsync("/api/entities/article-001/disable", null)) await ProblemAsync(absent, 404, factory);
        await ConsumerScenarios.SendAsync(cms, seconds: 3);
        using var recreated = await reader.GetAsync("/api/entities/article-001");
        Assert.Equal(200, (int)recreated.StatusCode);
        using var final = JsonDocument.Parse(await recreated.Content.ReadAsStringAsync());
        Assert.False(final.RootElement.GetProperty("adminDisabled").GetBoolean());
    }

    /// <summary>
    /// Correctly encoded opaque IDs remain distinct and usable in lookup and disabling without double-decoding percent sequences.
    /// </summary>
    /// <param name="id">The synthetic identifier containing reserved or Unicode characters.</param>
    /// <returns>A task completing after exact lookup and administrator mutation.</returns>
    [Theory]
    [InlineData("a/b")]
    [InlineData("a%2Fb")]
    [InlineData("ç+?%#")]
    public async Task OpaqueIdentifiersRoundTripThroughRoutes(string id)
    {
        await using var database = await fixture.CreateAsync();
        await using var factory = ConsumerScenarios.Factory(database);
        using var cms = Client(factory);
        using var admin = Client(factory, "admin");
        await ConsumerScenarios.SendAsync(cms, id);
        var route = "/api/entities/" + Uri.EscapeDataString(id);
        using var response = await ConsumerScenarios.SendRawTargetAsync(factory, HttpMethod.Get, route);
        Assert.Equal(200, (int)response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(id, body.RootElement.GetProperty("id").GetString());
        using var disabled = await ConsumerScenarios.SendRawTargetAsync(factory, HttpMethod.Put, route + "/disable");
        Assert.Equal(204, (int)disabled.StatusCode);
    }

    /// <summary>
    /// A cursor continues strictly after its source ID even if that row is deleted; role-mismatched cursors are rejected before reading.
    /// </summary>
    /// <returns>A task completing after deletion-safe continuation, visibility binding, and the terminal cursor are checked.</returns>
    [Fact]
    public async Task CursorsRemainContinuationsAndNeverGrantVisibility()
    {
        await using var database = await fixture.CreateAsync();
        await using var factory = ConsumerScenarios.Factory(database);
        using var cms = Client(factory);
        using var reader = Client(factory, "reader");
        using var admin = Client(factory, "admin");
        foreach (var id in new[] { "A", "B", "C" }) await ConsumerScenarios.SendAsync(cms, id);
        using var first = await reader.GetAsync("/api/entities?pageSize=1");
        using var firstBody = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var cursor = firstBody.RootElement.GetProperty("nextCursor").GetString();
        await ConsumerScenarios.SendAsync(cms, "A", seconds: 1, type: CmsEventType.Delete);
        using var next = await reader.GetAsync("/api/entities?pageSize=2&cursor=" + cursor);
        using var nextBody = JsonDocument.Parse(await next.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "B", "C" }, nextBody.RootElement.GetProperty("items").EnumerateArray().Select(value => value.GetProperty("id").GetString()));
        Assert.Equal(JsonValueKind.Null, nextBody.RootElement.GetProperty("nextCursor").ValueKind);
        using var wrongScope = await admin.GetAsync("/api/entities?cursor=" + cursor);
        await ProblemAsync(wrongScope, 400, factory);
    }

    /// <summary>
    /// An empty store has an explicit empty page and indistinguishable absent lookup/disable responses, with no deletion metadata exposed.
    /// </summary>
    /// <returns>A task completing after empty shape and missing-entity behavior are checked.</returns>
    [Fact]
    public async Task EmptyAndMissingStatesHaveConsistentResponses()
    {
        await using var database = await fixture.CreateAsync();
        await using var factory = ConsumerScenarios.Factory(database);
        using var admin = Client(factory, "admin");
        using var page = await admin.GetAsync("/api/entities");
        Assert.Equal("{\"items\":[],\"nextCursor\":null}", await page.Content.ReadAsStringAsync());
        using var missing = await admin.GetAsync("/api/entities/missing");
        await ProblemAsync(missing, 404, factory);
        using var disable = await admin.PutAsync("/api/entities/missing/disable", null);
        await ProblemAsync(disable, 404, factory);
    }
}
