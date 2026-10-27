using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace CmsEventProcessor.IntegrationTests;

/// <summary>
/// Exercises authentication, authorization, transport restrictions, and safe failures through real HTTP middleware.
/// </summary>
public sealed class AuthenticationHttpTests
{
    /// <summary>
    /// CMS routes accept only CMS credentials; consumer routes accept readers/admins; administrator routes
    /// reject readers with 403 and cross-scheme identities with 401. Fallback authorization also denies unknown users.
    /// </summary>
    /// <param name="route">The test-only policy endpoint exercised by this case.</param>
    /// <param name="kind">The supplied configured identity, or the unknown-user negative case.</param>
    /// <param name="expected">The expected HTTP result for that route and identity.</param>
    /// <returns>A task that completes after access status, cache policy, and any error response are verified.</returns>
    [Theory]
    [InlineData("cms", "cms", 200)]
    [InlineData("cms", "reader", 401)]
    [InlineData("cms", "admin", 401)]
    [InlineData("consumer", "cms", 401)]
    [InlineData("consumer", "reader", 200)]
    [InlineData("consumer", "admin", 200)]
    [InlineData("admin", "cms", 401)]
    [InlineData("admin", "reader", 403)]
    [InlineData("admin", "admin", 200)]
    [InlineData("fallback", "reader", 200)]
    [InlineData("fallback", "unknown", 401)]
    public async Task AccessMatrixIsEnforced(string route, string kind, int expected)
    {
        await using var factory = new FoundationFactory();
        using var client = factory.Client();
        SetCredentials(client, TestAccounts.Username(kind), TestAccounts.Password(kind));
        using var response = await client.GetAsync($"/_test/{route}");
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        if (expected >= 400)
        {
            await AssertProblem(response, expected);
        }
    }

    /// <summary>
    /// Requests without credentials receive correlated 401 problems and the correct CMS or consumer Basic realm.
    /// </summary>
    /// <param name="route">The explicitly protected or fallback-protected endpoint.</param>
    /// <param name="realm">The realm expected in its authentication challenge.</param>
    /// <returns>A task that completes after the 401 Problem Details and scheme-specific challenge are verified.</returns>
    [Theory]
    [InlineData("cms", "cms")]
    [InlineData("consumer", "consumers")]
    [InlineData("admin", "consumers")]
    [InlineData("fallback", "consumers")]
    public async Task MissingCredentialsChallengeWithoutExposingContent(string route, string realm)
    {
        await using var factory = new FoundationFactory();
        using var client = factory.Client();
        using var response = await client.GetAsync($"/_test/{route}");
        await AssertProblem(response, 401);
        Assert.Contains(response.Headers.WwwAuthenticate, header => header.Scheme == "Basic" && header.Parameter!.Contains($"realm=\"{realm}\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// HTML/XML Accept headers still receive safe Problem Details for authentication and unexpected failures.
    /// </summary>
    /// <param name="accept">A media type not handled by the framework's default JSON problem writer.</param>
    /// <param name="authenticate">Whether to exercise the authenticated exception route instead of a missing-credential challenge.</param>
    /// <param name="expected">The original error status that content negotiation must preserve.</param>
    /// <returns>A task that completes after the original error status and sanitized JSON problem are verified.</returns>
    [Theory]
    [InlineData("text/html", false, 401)]
    [InlineData("application/xml", false, 401)]
    [InlineData("text/html", true, 500)]
    [InlineData("application/xml", true, 500)]
    public async Task UnsupportedAcceptHeadersStillReceiveSafeProblems(string accept, bool authenticate, int expected)
    {
        await using var factory = new FoundationFactory();
        using var client = factory.Client();
        client.DefaultRequestHeaders.Accept.ParseAdd(accept);
        if (authenticate)
        {
            SetCredentials(client, TestAccounts.Username("reader"), TestAccounts.ReaderPassword);
        }
        using var response = await client.GetAsync(authenticate ? "/_test/failure" : "/_test/consumer");
        await AssertProblem(response, expected);
        Assert.DoesNotContain("CONFIDENTIAL_EXCEPTION_CONTENT", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Invalid Base64/UTF-8, missing credentials/separators, and unsupported schemes return 401 rather than 500.
    /// </summary>
    /// <param name="header">The malformed or unsupported Authorization value supplied by this case.</param>
    /// <returns>A task that completes after malformed credentials produce a safe 401 response.</returns>
    [Theory]
    [InlineData("Basic not-base64!")]
    [InlineData("Basic")]
    [InlineData("Bearer unrelated-token")]
    [InlineData("Basic /w==")]
    [InlineData("Basic bm9jb2xvbg==")]
    [InlineData("Basic OnBhc3N3b3Jk")]
    [InlineData("Basic dXNlcjo=")]
    public async Task MalformedCredentialsReturn401InsteadOf500(string header)
    {
        await using var factory = new FoundationFactory();
        using var client = factory.Client();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", header);
        using var response = await client.GetAsync("/_test/consumer");
        await AssertProblem(response, 401);
    }

    /// <summary>
    /// A wrong password is rejected, and a valid reader cannot become an administrator through an X-Role header.
    /// </summary>
    /// <returns>A task that completes after password rejection and resistance to client-supplied role escalation are verified.</returns>
    [Fact]
    public async Task WrongPasswordAndClientSuppliedRoleCannotEscalatePrivileges()
    {
        await using var factory = new FoundationFactory();
        using var client = factory.Client();
        SetCredentials(client, TestAccounts.Username("reader"), "wrong-password");
        using var wrong = await client.GetAsync("/_test/consumer");
        await AssertProblem(wrong, 401);
        SetCredentials(client, TestAccounts.Username("reader"), TestAccounts.ReaderPassword);
        client.DefaultRequestHeaders.Add("X-Role", "Admin");
        using var escalated = await client.GetAsync("/_test/admin");
        await AssertProblem(escalated, 403);
    }

    /// <summary>
    /// Multiple Authorization values and a header exceeding the parser's bound both fail with sanitized 401 responses.
    /// </summary>
    /// <returns>A task that completes after duplicate and oversized headers are both rejected with safe 401 responses.</returns>
    [Fact]
    public async Task DuplicateAndOversizedAuthorizationHeadersAreRejected()
    {
        await using var factory = new FoundationFactory();
        using var client = factory.Client();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", new[] { "Basic dXNlcjpwYXNz", "Basic dXNlcjpwYXNz" });
        using var duplicate = await client.GetAsync("/_test/consumer");
        await AssertProblem(duplicate, 401);
        client.DefaultRequestHeaders.Remove("Authorization");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Basic " + new string('A', 4096));
        using var oversized = await client.GetAsync("/_test/consumer");
        await AssertProblem(oversized, 401);
    }

    /// <summary>
    /// HTTP is rejected by default and for remote peers; explicitly enabled IPv4/IPv6 loopback requests succeed.
    /// An untrusted forwarded HTTPS header cannot bypass the socket-peer restriction.
    /// </summary>
    /// <param name="allow">Whether local HTTP is explicitly enabled in test configuration.</param>
    /// <param name="remote">The socket peer presented to production transport middleware.</param>
    /// <param name="expected">The HTTP status expected for the configuration/peer combination.</param>
    /// <returns>A task that completes after loopback restrictions and forwarded-header bypass resistance are checked.</returns>
    [Theory]
    [InlineData(false, "127.0.0.1", 400)]
    [InlineData(true, "127.0.0.1", 200)]
    [InlineData(true, "::1", 200)]
    [InlineData(true, "203.0.113.10", 400)]
    public async Task HttpIsLimitedToExplicitlyEnabledLoopback(bool allow, string remote, int expected)
    {
        await using var factory = new FoundationFactory { AllowLoopbackHttp = allow, RemoteAddress = remote };
        using var client = factory.Client("http");
        SetCredentials(client, TestAccounts.Username("reader"), TestAccounts.ReaderPassword);
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        using var response = await client.GetAsync("/_test/consumer");
        Assert.Equal(expected, (int)response.StatusCode);
    }

    /// <summary>
    /// Exception and conversion-error sentinels stay out of Problem Details and captured logs, as do valid passwords.
    /// </summary>
    /// <returns>A task that completes after error bodies and captured logs are checked for confidential values and passwords.</returns>
    [Fact]
    public async Task ErrorsAndValidationDoNotExposeSensitiveValues()
    {
        await using var factory = new FoundationFactory();
        using var client = factory.Client();
        SetCredentials(client, TestAccounts.Username("reader"), TestAccounts.ReaderPassword);
        using var failure = await client.GetAsync("/_test/failure");
        await AssertProblem(failure, 500);
        Assert.DoesNotContain("CONFIDENTIAL_EXCEPTION_CONTENT", await failure.Content.ReadAsStringAsync());
        using var input = new StringContent("{\"count\":\"CONFIDENTIAL_REQUEST_CONTENT\"}", Encoding.UTF8, "application/json");
        using var invalid = await client.PostAsync("/_test/validation", input);
        await AssertProblem(invalid, 400);
        Assert.DoesNotContain("CONFIDENTIAL_REQUEST_CONTENT", await invalid.Content.ReadAsStringAsync());
        var messages = string.Join('\n', factory.Logs.Messages);
        Assert.DoesNotContain("CONFIDENTIAL_EXCEPTION_CONTENT", messages);
        Assert.DoesNotContain("CONFIDENTIAL_REQUEST_CONTENT", messages);
        Assert.DoesNotContain(TestAccounts.ReaderPassword, messages);
    }

    /// <summary>
    /// The host refuses to start when no configured CMS, reader, or administrator identities are available.
    /// </summary>
    /// <returns>A task that completes after the missing-account startup failure is asserted and the factory is disposed.</returns>
    [Fact]
    public async Task StartupRejectsMissingAccounts()
    {
        await using var factory = new FoundationFactory { MissingAccounts = true };
        Assert.Throws<OptionsValidationException>(() => factory.Client());
    }

    /// <summary>
    /// Without test assembly registration, protected probe routes are absent from the production application.
    /// </summary>
    /// <param name="path">The test-only route whose absence is verified.</param>
    /// <returns>A task that completes after the production application rejects the unmapped route with 404.</returns>
    [Theory]
    [InlineData("/_test/consumer")]
    public async Task ProductionHostDoesNotShipTestEndpoints(string path)
    {
        await using var factory = new FoundationFactory { IncludeTestControllers = false };
        using var client = factory.Client();
        SetCredentials(client, TestAccounts.Username("reader"), TestAccounts.ReaderPassword);
        using var response = await client.GetAsync(path);
        await AssertProblem(response, 404);
    }

    /// <summary>
    /// Attaches an ephemeral test identity using the standard UTF-8 Basic credential encoding.
    /// </summary>
    /// <param name="client">The test client whose Authorization header is replaced.</param>
    /// <param name="username">The ephemeral fixture username to encode.</param>
    /// <param name="password">The ephemeral fixture password to encode without logging it.</param>
    internal static void SetCredentials(HttpClient client, string username, string password) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));

    /// <summary>
    /// Checks the expected failure status, Problem Details media type, body status, and nonempty trace correlation.
    /// </summary>
    /// <param name="response">The HTTP failure response to inspect.</param>
    /// <param name="status">The status expected in both the HTTP response and problem body.</param>
    /// <returns>A task that completes after media type, status consistency, and trace correlation assertions pass.</returns>
    private static async Task AssertProblem(HttpResponseMessage response, int status)
    {
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(status, body.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("traceId").GetString()));
    }
}
