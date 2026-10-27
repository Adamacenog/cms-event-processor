using System.Text;
using static CmsEventProcessor.IntegrationTests.Webhook.WebhookScenarios;

namespace CmsEventProcessor.IntegrationTests.Entities;

/// <summary>
/// Verifies consumer authorization and validation through production endpoints before deliberately unavailable storage is opened.
/// </summary>
public sealed class ConsumerValidationHttpTests
{
    /// <summary>
    /// Anonymous/CMS users cannot read consumer content, and readers cannot invoke the administrator operation.
    /// </summary>
    /// <param name="method">The production HTTP operation.</param>
    /// <param name="route">The consumer route.</param>
    /// <param name="kind">The configured credential selector or missing-credential case.</param>
    /// <param name="status">The expected challenge or forbid status.</param>
    /// <returns>A task completing after status and safe response validation.</returns>
    [Theory]
    [InlineData("GET", "/api/entities", "missing", 401)]
    [InlineData("GET", "/api/entities", "cms", 401)]
    [InlineData("GET", "/api/entities/X", "cms", 401)]
    [InlineData("PUT", "/api/entities/X/disable", "cms", 401)]
    [InlineData("PUT", "/api/entities/X/disable", "missing", 401)]
    [InlineData("PUT", "/api/entities/X/disable", "reader", 403)]
    [InlineData("GET", "/api/entities", "unknown", 401)]
    public async Task RealConsumerAccessMatrixIsEnforced(string method, string route, string kind, int status)
    {
        await using var factory = new FoundationFactory { IncludeTestControllers = false };
        using var client = kind == "missing" ? factory.Client() : Client(factory, kind);
        using var request = new HttpRequestMessage(new HttpMethod(method), route);
        using var response = await client.SendAsync(request);
        await ProblemAsync(response, status, factory);
        if (status == 401) Assert.Contains(response.Headers.WwwAuthenticate, value => value.Parameter!.Contains("consumer", StringComparison.Ordinal));
    }

    /// <summary>
    /// Invalid paging, duplicate fields, forged visibility, malformed cursors, and invalid IDs return safe 400s before storage.
    /// </summary>
    /// <param name="path">The invalid consumer request path/query.</param>
    /// <returns>A task completing after safe validation error and unsupported Accept handling are checked.</returns>
    [Theory]
    [InlineData("/api/entities?pageSize=0")]
    [InlineData("/api/entities?pageSize=101")]
    [InlineData("/api/entities?pageSize=1&pageSize=2")]
    [InlineData("/api/entities?includeHidden=true")]
    [InlineData("/api/entities?cursor=CONFIDENTIAL_CURSOR")]
    [InlineData("/api/entities?cursor=")]
    [InlineData("/api/entities/%20invalid")]
    public async Task InvalidInputDoesNotOpenSql(string path)
    {
        await using var factory = new FoundationFactory();
        using var client = Client(factory, "reader");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        using var response = await client.GetAsync(path);
        var problem = await ProblemAsync(response, 400, factory);
        Assert.True(problem.TryGetProperty("errors", out _));
    }

    /// <summary>
    /// Both declared-length and streaming bodies are rejected by administrator disabling instead of allowing content updates.
    /// </summary>
    /// <param name="streaming">Whether the body omits a computed Content-Length.</param>
    /// <returns>A task completing after body rejection before any SQL mutation.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisableRejectsAllNonemptyBodies(bool streaming)
    {
        await using var factory = new FoundationFactory();
        using var client = Client(factory, "admin");
        var bytes = "{\"payload\":\"CONFIDENTIAL_WEBHOOK_CONTENT\"}"u8.ToArray();
        using HttpContent body = streaming ? new Webhook.UnknownLengthContent(bytes) : new ByteArrayContent(bytes);
        using var response = await client.PutAsync("/api/entities/X/disable", body);
        await ProblemAsync(response, 400, factory);
    }

    /// <summary>
    /// Source IDs and opaque cursors in URLs do not escape through framework request, routing, or action diagnostics.
    /// </summary>
    /// <returns>A task completing after a no-storage invalid request and complete captured-log privacy assertions.</returns>
    [Fact]
    public async Task RawIdentifiersAndCursorsAreAbsentFromLogs()
    {
        await using var factory = new FoundationFactory();
        using var client = Client(factory, "reader");
        using var response = await client.GetAsync("/api/entities/%20CONFIDENTIAL_SOURCE_ID?cursor=CONFIDENTIAL_CURSOR");
        await ProblemAsync(response, 400, factory);
        var logs = string.Join('\n', factory.Logs.Messages);
        Assert.DoesNotContain("CONFIDENTIAL_SOURCE_ID", logs);
        Assert.DoesNotContain("CONFIDENTIAL_CURSOR", logs);
    }

    /// <summary>
    /// Every configured account can read the full generated reference while operation security retains separate CMS/consumer schemes.
    /// </summary>
    /// <param name="kind">The configured account kind.</param>
    /// <returns>A task completing after authenticated reference access is checked.</returns>
    [Theory]
    [InlineData("cms")]
    [InlineData("reader")]
    [InlineData("admin")]
    public async Task ConfiguredUsersCanReadAuthenticatedReference(string kind)
    {
        await using var factory = new FoundationFactory();
        using var client = Client(factory, kind);
        using var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(200, (int)response.StatusCode);
    }

    /// <summary>
    /// The shared API reference remains private and returns a Basic challenge when no configured credentials are supplied.
    /// </summary>
    /// <returns>A task completing after the safe 401 and authentication challenge are checked.</returns>
    [Fact]
    public async Task ApiReferenceRequiresAuthentication()
    {
        await using var factory = new FoundationFactory();
        using var client = factory.Client();
        using var response = await client.GetAsync("/openapi/v1.json");
        await ProblemAsync(response, 401, factory);
        Assert.Contains(response.Headers.WwwAuthenticate, value => value.Scheme == "Basic");
    }

    /// <summary>
    /// Consumers have no create/update/delete-content action, including administrator identities with valid credentials.
    /// </summary>
    /// <param name="method">The unsupported content-mutation method.</param>
    /// <param name="path">The collection or entity route.</param>
    /// <returns>A task completing after safe 405 behavior without storage access.</returns>
    [Theory]
    [InlineData("POST", "/api/entities")]
    [InlineData("PUT", "/api/entities/X")]
    [InlineData("PATCH", "/api/entities/X")]
    [InlineData("DELETE", "/api/entities/X")]
    public async Task ContentMutationRoutesAreAbsent(string method, string path)
    {
        await using var factory = new FoundationFactory();
        using var admin = Client(factory, "admin");
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await admin.SendAsync(request);
        await ProblemAsync(response, 405, factory);
    }
}
