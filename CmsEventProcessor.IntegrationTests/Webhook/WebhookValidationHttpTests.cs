using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using CmsEventProcessor.Api.Cms;
using static CmsEventProcessor.IntegrationTests.Webhook.WebhookScenarios;

namespace CmsEventProcessor.IntegrationTests.Webhook;

/// <summary>
/// Exercises the production webhook's authentication, complete-input validation, bounded reading, and safe errors without SQL.
/// </summary>
public sealed class WebhookValidationHttpTests
{
    /// <summary>
    /// Supplies malformed documents, strict-envelope mistakes, and invalid field values that must never reach storage.
    /// </summary>
    /// <returns>Invalid synthetic bodies and the fixed error path expected for each case.</returns>
    public static IEnumerable<object[]> InvalidBodies()
    {
        foreach (var body in new[] { "", "null", "{}", "[]", "[null]", "[1]", "[", "[] []", "[/*comment*/{}]", "[{},]",
            "[{\"type\":\"delete\",\"type\":\"publish\"}]",
            ValidBody.Replace("\"title\":", "\"title\":1,\"t\\u0069tle\":"),
            ValidBody.Replace("\"title\":\"CONFIDENTIAL_WEBHOOK_CONTENT\"", "\"array\":[{\"x\":1,\"x\":2}]") })
        {
            yield return [body, "$orEvent"];
        }
        var invalid = new (string Field, JsonNode? Value)[]
        {
            ("type", "update"), ("type", "1"), ("type", 1), ("type", null),
            ("id", ""), ("id", " leading"), ("id", "trailing "), ("id", "line\nfeed"), ("id", new string('x',129)), ("id", 1), ("id", null),
            ("timestamp", "2026-01-01T12:00:00"), ("timestamp", "2026-02-30T12:00:00Z"),
            ("timestamp", "2026-01-01"), ("timestamp", "2026-01-01T12:00:00.12345678Z"), ("timestamp", 1), ("timestamp", null),
            ("version", 0), ("version", -1), ("version", "1"), ("version", 1.5), ("version", null),
            ("payload", "CONFIDENTIAL_WEBHOOK_CONTENT"), ("payload", new JsonArray()), ("payload", 1), ("payload", null)
        };
        foreach (var (field, value) in invalid)
        {
            var input = JsonNode.Parse(ValidBody)!;
            input[0]![field] = value?.DeepClone();
            yield return [input.ToJsonString(), $"[0].{field}"];
        }
        foreach (var field in new[] { "type", "id", "timestamp", "version", "payload" })
        {
            var input = JsonNode.Parse(ValidBody)!;
            input[0]!.AsObject().Remove(field);
            yield return [input.ToJsonString(), $"[0].{field}"];
        }
        yield return [ValidBody.Replace("\"id\":", "\"CONFIDENTIAL_UNKNOWN_PROPERTY\":0,\"id\":"), "[0]"];
        yield return [ValidBody.Replace("\"id\":", "\"Id\":"), "[0].id"];
        yield return [ValidBody.Replace("\"version\":1", "\"version\":9223372036854775808"), "[0].version"];
        yield return [ValidBody.Replace("CONFIDENTIAL_WEBHOOK_CONTENT", "\\ud800"), "$"];
        yield return ["[" + string.Join(',', Enumerable.Repeat(ValidBody[1..^1], 101)) + "]", "$"];
        yield return [ValidBody.Replace("\"title\":\"CONFIDENTIAL_WEBHOOK_CONTENT\"", "\"x\":" + new string('[', 33) + "0" + new string(']', 33)), "$"];
    }

    /// <summary>
    /// Every malformed or invalid event returns sanitized validation errors before the deliberately unavailable writer is opened.
    /// </summary>
    /// <param name="body">The invalid synthetic UTF-8 document.</param>
    /// <param name="field">The fixed field path expected, or a document/event error marker.</param>
    /// <returns>A task completing after safe 400 responses and log privacy are verified.</returns>
    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task InvalidInputNeverReachesStorage(string body, string field)
    {
        await using var factory = new FoundationFactory();
        using var client = Client(factory);
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        using var response = await PostAsync(client, body);
        var problem = await ProblemAsync(response, 400, factory);
        Assert.True(problem.TryGetProperty("errors", out var errors));
        if (field != "$orEvent") Assert.True(errors.TryGetProperty(field, out _));
        Assert.DoesNotContain("CONFIDENTIAL_UNKNOWN_PROPERTY", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Missing, malformed, unknown, incorrect-password, and consumer credentials cannot reach CMS ingestion or its documentation.
    /// </summary>
    /// <param name="kind">The credential scenario.</param>
    /// <param name="route">The production ingestion or OpenAPI route.</param>
    /// <returns>A task completing after the CMS challenge and safe 401 are checked.</returns>
    [Theory]
    [InlineData("missing", "/cms/events")]
    [InlineData("malformed", "/cms/events")]
    [InlineData("unknown", "/cms/events")]
    [InlineData("wrong", "/cms/events")]
    [InlineData("reader", "/cms/events")]
    [InlineData("admin", "/cms/events")]
    [InlineData("missing", "/openapi/v1.json")]
    [InlineData("reader", "/openapi/v1.json")]
    public async Task CmsAuthenticationPrecedesInputValidation(string kind, string route)
    {
        await using var factory = new FoundationFactory { IncludeTestControllers = false };
        using var client = factory.Client();
        if (kind == "malformed") client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Basic not-base64!");
        else if (kind != "missing") AuthenticationHttpTests.SetCredentials(client,
            TestAccounts.Username(kind == "wrong" ? "cms" : kind), kind == "wrong" ? "incorrect-password" : TestAccounts.Password(kind));
        using var response = route == "/cms/events" ? await PostAsync(client, "invalid") : await client.GetAsync(route);
        await ProblemAsync(response, 401, factory);
        Assert.Contains(response.Headers.WwwAuthenticate, value => value.Parameter!.Contains("realm=\"cms\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// Oversized bodies return 413 whether length is declared or only discovered during streaming.
    /// </summary>
    /// <param name="knownLength">Whether the transport advertises Content-Length.</param>
    /// <returns>A task completing after byte-based limits and safe Problem Details are checked.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BodyLimitDoesNotDependOnContentLength(bool knownLength)
    {
        await using var factory = new FoundationFactory();
        using var client = Client(factory);
        var bytes = Encoding.UTF8.GetBytes(ValidBody.PadRight(WebhookLimits.BodyBytes + 1));
        using HttpContent content = knownLength ? new ByteArrayContent(bytes) : new UnknownLengthContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await client.PostAsync("/cms/events", content);
        await ProblemAsync(response, 413, factory);
    }

    /// <summary>
    /// Unsupported media types, character sets, and compressed representations return 415 before reading or processing.
    /// </summary>
    /// <param name="mediaType">The declared request content type.</param>
    /// <param name="encoding">Optional unsupported Content-Encoding.</param>
    /// <returns>A task completing after safe 415 responses are verified.</returns>
    [Theory]
    [InlineData("text/plain", null)]
    [InlineData("application/json; charset=utf-16", null)]
    [InlineData("application/problem+json", null)]
    [InlineData("application/json", "gzip")]
    [InlineData(null, null)]
    public async Task UnsupportedRepresentationIsRejected(string? mediaType, string? encoding)
    {
        await using var factory = new FoundationFactory();
        using var client = Client(factory);
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(ValidBody));
        if (mediaType is not null) content.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        if (encoding is not null) content.Headers.ContentEncoding.Add(encoding);
        using var response = await client.PostAsync("/cms/events", content);
        await ProblemAsync(response, 415, factory);
    }

    /// <summary>
    /// Valid UTF-8 media parameters, including quoted/case-insensitive charset values, reach JSON validation instead of a false 415.
    /// </summary>
    /// <param name="mediaType">A valid application/json media type with a supported charset representation.</param>
    /// <returns>A task completing after the empty array is correctly classified as a 400 validation problem.</returns>
    [Theory]
    [InlineData("application/json; charset=\"utf-8\"")]
    [InlineData("application/json; charset=UTF-8")]
    [InlineData("APPLICATION/JSON")]
    public async Task ValidJsonMediaParametersReachValidation(string mediaType)
    {
        await using var factory = new FoundationFactory();
        using var client = Client(factory);
        using var content = new ByteArrayContent("[]"u8.ToArray());
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        using var response = await client.PostAsync("/cms/events", content);
        await ProblemAsync(response, 400, factory);
    }

    /// <summary>
    /// Blank, overlong, and repeated idempotency headers are rejected without touching the database.
    /// </summary>
    /// <param name="scenario">The invalid header shape to send.</param>
    /// <returns>A task completing after the fixed key field error is verified.</returns>
    [Theory]
    [InlineData("empty")]
    [InlineData("long")]
    [InlineData("repeated")]
    public async Task InvalidKeysReturnValidationProblems(string scenario)
    {
        await using var factory = new FoundationFactory();
        using var client = Client(factory);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Idempotency-Key",
            scenario == "repeated" ? new[] { "one", "two" } : new[] { scenario == "long" ? new string('k', 129) : " " });
        using var response = await PostAsync(client);
        var problem = await ProblemAsync(response, 400, factory);
        Assert.True(problem.GetProperty("errors").TryGetProperty("Idempotency-Key", out _));
    }

    /// <summary>
    /// Invalid raw UTF-8 returns a field-validation problem instead of an unexpected string-decoding exception.
    /// </summary>
    /// <returns>A task completing after safe rejection of non-UTF-8 body bytes.</returns>
    [Fact]
    public async Task InvalidUtf8Returns400()
    {
        await using var factory = new FoundationFactory();
        using var client = Client(factory);
        var bytes = Encoding.UTF8.GetBytes(ValidBody);
        bytes[Array.IndexOf(bytes, (byte)'a')] = 0xff;
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await client.PostAsync("/cms/events", content);
        var problem = await ProblemAsync(response, 400, factory);
        Assert.True(problem.TryGetProperty("errors", out _));
    }
}
