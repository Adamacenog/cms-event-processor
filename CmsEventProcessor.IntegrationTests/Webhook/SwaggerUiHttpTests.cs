using System.Text.Json;
using static CmsEventProcessor.IntegrationTests.Webhook.WebhookScenarios;

namespace CmsEventProcessor.IntegrationTests.Webhook;

/// <summary>
/// Verifies development documentation assets, authentication, and exclusion from other environments.
/// </summary>
public sealed class SwaggerUiHttpTests
{
    /// <summary>
    /// Every configured account can open the UI, load its local assets, and retrieve the existing protected specification.
    /// </summary>
    /// <param name="kind">The CMS, reader, or administrator identity opening documentation.</param>
    /// <returns>A task completing after the redirect, assets, safe configuration, and specification are verified.</returns>
    [Theory]
    [InlineData("cms")]
    [InlineData("reader")]
    [InlineData("admin")]
    public async Task DevelopmentUiAcceptsAllDocumentationIdentities(string kind)
    {
        await using var factory = new FoundationFactory { EnvironmentName = "Development" };
        using var client = Client(factory, kind);
        foreach (var route in new[] { "/swagger", "/swagger/" })
        {
            using var redirect = await client.GetAsync(route);
            Assert.Equal(301, (int)redirect.StatusCode);
            Assert.EndsWith("index.html", redirect.Headers.Location?.ToString());
            Assert.Equal("no-store", redirect.Headers.CacheControl?.ToString());
        }

        foreach (var route in new[] { "/swagger/index.html", "/swagger/index.js", "/swagger/swagger-ui.css",
            "/swagger/swagger-ui-bundle.js", "/swagger/swagger-ui-standalone-preset.js" })
        {
            using var asset = await client.GetAsync(route);
            Assert.Equal(200, (int)asset.StatusCode);
            Assert.Contains("no-store", asset.Headers.CacheControl?.ToString());
            var text = await asset.Content.ReadAsStringAsync();
            Assert.NotEmpty(text);
            Assert.DoesNotContain(TestAccounts.Password(kind), text);
            if (route.EndsWith("index.html", StringComparison.Ordinal))
            {
                Assert.Equal("text/html", asset.Content.Headers.ContentType?.MediaType);
                Assert.Contains("CMS Event Processor API", text);
            }
            if (route.EndsWith("index.js", StringComparison.Ordinal))
            {
                Assert.Contains("../openapi/v1.json", text);
                Assert.Contains("\"persistAuthorization\":false", text);
                Assert.Contains("configObject.validatorUrl = null", text);
            }
        }

        using var specification = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(200, (int)specification.StatusCode);
        using var document = JsonDocument.Parse(await specification.Content.ReadAsStringAsync());
        Assert.Equal(6, document.RootElement.GetProperty("paths").EnumerateObject().Count());
        Assert.DoesNotContain("swagger", document.RootElement.GetProperty("paths").ToString());
    }

    /// <summary>
    /// Missing, malformed, and incorrect credentials cannot retrieve any development UI asset or its specification.
    /// </summary>
    /// <param name="credentials">The unauthenticated or invalid credential scenario.</param>
    /// <returns>A task completing after every documentation resource returns a safe Basic challenge.</returns>
    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("wrong")]
    public async Task DocumentationRequiresValidCredentials(string credentials)
    {
        await using var factory = new FoundationFactory { EnvironmentName = "Development" };
        using var client = factory.Client();
        if (credentials == "malformed") client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Basic not-base64!");
        if (credentials == "wrong") AuthenticationHttpTests.SetCredentials(client, TestAccounts.Username("reader"), "incorrect-password");
        foreach (var route in new[] { "/swagger", "/swagger/index.html", "/swagger/index.js", "/swagger/swagger-ui.css",
            "/swagger/swagger-ui-bundle.js", "/swagger/swagger-ui-standalone-preset.js", "/openapi/v1.json" })
        {
            using var response = await client.GetAsync(route);
            await ProblemAsync(response, 401, factory);
            Assert.Contains(response.Headers.WwwAuthenticate, header => header.Scheme == "Basic");
        }
    }

    /// <summary>
    /// Non-development hosts expose no Swagger UI resources while preserving authenticated OpenAPI access.
    /// </summary>
    /// <param name="environment">The non-development host environment.</param>
    /// <returns>A task completing after UI routes return 404 and the existing specification remains available.</returns>
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public async Task OtherEnvironmentsDoNotExposeSwaggerUi(string environment)
    {
        await using var factory = new FoundationFactory { EnvironmentName = environment };
        using var client = Client(factory, "reader");
        foreach (var route in new[] { "/swagger", "/swagger/index.html", "/swagger/index.js", "/swagger/swagger-ui.css",
            "/swagger/swagger-ui-bundle.js", "/swagger/swagger-ui-standalone-preset.js" })
        {
            using var response = await client.GetAsync(route);
            await ProblemAsync(response, 404, factory);
        }
        using var specification = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(200, (int)specification.StatusCode);
    }

    /// <summary>
    /// Opening documentation does not grant CMS access to consumer data or reader access to ingestion/administrator actions.
    /// </summary>
    /// <param name="kind">The documentation identity making a forbidden business request.</param>
    /// <param name="method">The business endpoint's HTTP verb.</param>
    /// <param name="route">The endpoint outside the identity's permissions.</param>
    /// <param name="status">The expected authentication or authorization failure.</param>
    /// <returns>A task completing after the UI opens and the business request is still rejected before SQL access.</returns>
    [Theory]
    [InlineData("cms", "GET", "/api/entities", 401)]
    [InlineData("reader", "POST", "/cms/events", 401)]
    [InlineData("admin", "POST", "/cms/events", 401)]
    [InlineData("reader", "PUT", "/api/entities/article-001/disable", 403)]
    [InlineData("reader", "GET", "/health/live", 403)]
    public async Task DocumentationDoesNotExpandBusinessPermissions(string kind, string method, string route, int status)
    {
        await using var factory = new FoundationFactory { EnvironmentName = "Development" };
        using var client = Client(factory, kind);
        using var ui = await client.GetAsync("/swagger/index.html");
        Assert.Equal(200, (int)ui.StatusCode);
        using var request = new HttpRequestMessage(new HttpMethod(method), route);
        using var response = await client.SendAsync(request);
        await ProblemAsync(response, status, factory);
    }

    /// <summary>
    /// Development documentation retains the external HTTPS requirement even when loopback HTTP is permitted.
    /// </summary>
    /// <returns>A task completing after a non-loopback HTTP request is rejected before serving the UI.</returns>
    [Fact]
    public async Task DevelopmentUiRetainsTransportRestrictions()
    {
        await using var factory = new FoundationFactory
        {
            EnvironmentName = "Development",
            AllowLoopbackHttp = true,
            RemoteAddress = "192.0.2.1"
        };
        using var client = factory.Client("http");
        AuthenticationHttpTests.SetCredentials(client, TestAccounts.Username("reader"), TestAccounts.ReaderPassword);
        using var response = await client.GetAsync("/swagger/index.html");
        await ProblemAsync(response, 400, factory);
    }
}
