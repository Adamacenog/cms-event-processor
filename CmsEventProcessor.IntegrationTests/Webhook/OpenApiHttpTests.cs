using System.Text.Json;
using static CmsEventProcessor.IntegrationTests.Webhook.WebhookScenarios;

namespace CmsEventProcessor.IntegrationTests.Webhook;

/// <summary>
/// Checks generated documentation against production routes, transport DTOs, and distinct operation security schemes.
/// </summary>
public sealed class OpenApiHttpTests
{
    /// <summary>
    /// Configured identities retrieve implemented ingestion/consumer metadata with distinct security schemes and no future/test routes.
    /// </summary>
    /// <returns>A task completing after document generation and schema/reference/response assertions.</returns>
    [Fact]
    public async Task GeneratedDocumentDescribesOnlyImplementedEndpoints()
    {
        await using var factory = new FoundationFactory();
        using var client = Client(factory);
        using var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        Assert.Equal(4, root.GetProperty("paths").EnumerateObject().Count());
        var operation = root.GetProperty("paths").GetProperty("/cms/events").GetProperty("post");
        Assert.True(operation.GetProperty("security")[0].TryGetProperty("cmsBasic", out _));
        Assert.Equal("basic", root.GetProperty("components").GetProperty("securitySchemes").GetProperty("cmsBasic").GetProperty("scheme").GetString());
        var body = operation.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema");
        Assert.Equal(1, body.GetProperty("minItems").GetInt32());
        Assert.Equal(100, body.GetProperty("maxItems").GetInt32());
        var item = body.GetProperty("items");
        if (item.TryGetProperty("$ref", out var reference)) item = Resolve(root, reference.GetString()!);
        Assert.False(item.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("object", item.GetProperty("properties").GetProperty("payload").GetProperty("type").GetString());
        Assert.Equal(2, item.GetProperty("oneOf").GetArrayLength());
        foreach (var status in new[] { "200", "400", "401", "409", "413", "415", "500", "503" })
            Assert.True(operation.GetProperty("responses").TryGetProperty(status, out _));
        Assert.DoesNotContain("/_test", text);
        var consumers = root.GetProperty("paths").GetProperty("/api/entities").GetProperty("get");
        Assert.True(consumers.GetProperty("security")[0].TryGetProperty("consumerBasic", out _));
        Assert.Equal(2, consumers.GetProperty("parameters").GetArrayLength());
        Assert.True(root.GetProperty("paths").GetProperty("/api/entities/{id}/disable").TryGetProperty("put", out _));
        Assert.DoesNotContain("/health/", text);
        Assert.DoesNotContain(TestAccounts.CmsPassword, text);
    }

    /// <summary>
    /// Resolves a generated local schema reference so assertions inspect the definition rather than serializer reuse details.
    /// </summary>
    /// <param name="root">The full generated document.</param>
    /// <param name="reference">The internal JSON pointer.</param>
    /// <returns>The referenced schema object.</returns>
    private static JsonElement Resolve(JsonElement root, string reference)
    {
        foreach (var segment in reference[2..].Split('/')) root = root.GetProperty(segment.Replace("~1", "/").Replace("~0", "~"));
        return root;
    }
}
