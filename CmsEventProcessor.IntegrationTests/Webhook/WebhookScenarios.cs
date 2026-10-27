using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CmsEventProcessor.Infrastructure.Persistence.Contexts;
using CmsEventProcessor.IntegrationTests.Sql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace CmsEventProcessor.IntegrationTests.Webhook;

/// <summary>
/// Supplies synthetic wire requests and isolated production HTTP hosts for webhook boundary tests.
/// </summary>
internal static class WebhookScenarios
{
    public const string ValidBody = """
        [{"type":"publish","id":"article-001","version":1,"timestamp":"2026-01-01T12:00:00Z","payload":{"title":"CONFIDENTIAL_WEBHOOK_CONTENT"}}]
        """;

    /// <summary>
    /// Sends exact bytes and an optional opaque key to the real production route.
    /// </summary>
    /// <param name="client">The authenticated test client.</param>
    /// <param name="body">The exact UTF-8 document text.</param>
    /// <param name="key">An optional key value.</param>
    /// <param name="cancellationToken">Optional caller cancellation.</param>
    /// <returns>The HTTP response, owned by the test.</returns>
    /// <exception cref="OperationCanceledException">The caller cancels the request.</exception>
    internal static async Task<HttpResponseMessage> PostAsync(HttpClient client, string body = ValidBody, string? key = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/cms/events")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }
        return await client.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Creates a test host pointing exclusively at this test's migrated SQL catalog with optional failure probes.
    /// </summary>
    /// <param name="database">The owned database shared only within the case.</param>
    /// <param name="interceptors">Optional deterministic EF transaction/save probes.</param>
    /// <returns>A disposable production-pipeline factory with no probe controllers.</returns>
    internal static FoundationFactory Factory(SqlTestDatabase database, params IInterceptor[] interceptors) => new()
    {
        IncludeTestControllers = false,
        Settings = new Dictionary<string, string?>
        {
            ["Persistence:WriteConnectionString"] = database.ConnectionString,
            ["Persistence:ReadConnectionString"] = database.ConnectionString
        },
        ConfigureTestServices = services =>
        {
            if (interceptors.Length > 0)
            {
                services.AddDbContextFactory<WriteContext>(builder => builder.AddInterceptors(interceptors));
            }
        }
    };

    /// <summary>
    /// Selects an ephemeral configured identity without accessing developer secrets.
    /// </summary>
    /// <param name="factory">The isolated host.</param>
    /// <param name="kind">The CMS, reader, or administrator test selector.</param>
    /// <returns>An authenticated HTTPS client owned by the test.</returns>
    internal static HttpClient Client(FoundationFactory factory, string kind = "cms")
    {
        var client = factory.Client();
        AuthenticationHttpTests.SetCredentials(client, TestAccounts.Username(kind), TestAccounts.Password(kind));
        return client;
    }

    /// <summary>
    /// Asserts uniform no-store correlated problems and rejects confidential sentinels in responses and all captured logs.
    /// </summary>
    /// <param name="response">The response whose status/media/body are checked.</param>
    /// <param name="status">The expected HTTP and problem status.</param>
    /// <param name="factory">The host whose full diagnostic output is inspected.</param>
    /// <param name="errorCode">An optional processing classification.</param>
    /// <returns>The cloned problem JSON for additional safe field assertions.</returns>
    internal static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, int status, FoundationFactory factory, string? errorCode = null)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        Assert.Equal(status, root.GetProperty("status").GetInt32());
        Assert.Equal("about:blank", root.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("traceId").GetString()));
        if (errorCode is not null)
        {
            Assert.Equal(errorCode, root.GetProperty("errorCode").GetString());
        }
        var messages = text + string.Join('\n', factory.Logs.Messages);
        Assert.DoesNotContain("CONFIDENTIAL_WEBHOOK_CONTENT", messages);
        Assert.DoesNotContain("CONFIDENTIAL_COMMIT_SENTINEL", messages);
        Assert.DoesNotContain(TestAccounts.CmsPassword, messages);
        return root.Clone();
    }
}
