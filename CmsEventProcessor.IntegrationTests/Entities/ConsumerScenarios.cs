using System.Text;
using CmsEventProcessor.Domain.Entities.Models;
using CmsEventProcessor.Infrastructure.Persistence.Contexts;
using CmsEventProcessor.IntegrationTests.Sql;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace CmsEventProcessor.IntegrationTests.Entities;

/// <summary>
/// Builds isolated production HTTP hosts and synthetic CMS requests for consumer acceptance scenarios.
/// </summary>
internal static class ConsumerScenarios
{
    /// <summary>
    /// Points both production connections at this case's owned catalog and attaches only explicit test probes.
    /// </summary>
    /// <param name="database">The uniquely named migrated catalog.</param>
    /// <param name="readProbe">An optional read command probe.</param>
    /// <param name="writeProbe">An optional commit/save probe.</param>
    /// <returns>A disposable host with no shipped test controllers.</returns>
    internal static FoundationFactory Factory(SqlTestDatabase database, IInterceptor? readProbe = null, IInterceptor? writeProbe = null) => new()
    {
        IncludeTestControllers = false,
        Settings = new()
        {
            ["Persistence:WriteConnectionString"] = database.ConnectionString,
            ["Persistence:ReadConnectionString"] = database.ConnectionString
        },
        ConfigureTestServices = services =>
        {
            if (readProbe is not null) services.AddDbContextFactory<ReadContext>(builder => builder.AddInterceptors(readProbe));
            if (writeProbe is not null) services.AddDbContextFactory<WriteContext>(builder => builder.AddInterceptors(writeProbe));
        }
    };

    /// <summary>
    /// Sends one valid synthetic source event and requires confirmed HTTP success before consumers read it.
    /// </summary>
    /// <param name="cms">The CMS-authenticated client.</param>
    /// <param name="id">The synthetic opaque identifier.</param>
    /// <param name="version">The source revision.</param>
    /// <param name="seconds">The source-clock offset.</param>
    /// <param name="type">The lifecycle action.</param>
    /// <returns>A task completing only after a successful committed ingestion response.</returns>
    internal static async Task SendAsync(HttpClient cms, string id = "article-001", long version = 1, int seconds = 0,
        CmsEventType type = CmsEventType.Publish)
    {
        var body = Encoding.UTF8.GetString(SqlScenarios.Body([SqlScenarios.Source(id, version, seconds, type,
            "{\"title\":\"CONFIDENTIAL_WEBHOOK_CONTENT\",\"number\":9007199254740993}")]));
        using var response = await Webhook.WebhookScenarios.PostAsync(cms, body);
        Assert.Equal(200, (int)response.StatusCode);
    }

    /// <summary>
    /// Supplies TestServer's omitted original target for encoded-slash cases; native Kestrel verification separately exercises actual wire routing.
    /// </summary>
    /// <param name="factory">The isolated production host.</param>
    /// <param name="method">The HTTP read or disable method.</param>
    /// <param name="rawTarget">The original once-encoded request path.</param>
    /// <returns>The buffered administrator-authenticated response, owned by the caller.</returns>
    internal static async Task<HttpResponseMessage> SendRawTargetAsync(FoundationFactory factory, HttpMethod method, string rawTarget)
    {
        using var client = new HttpClient(factory.Server.CreateHandler(context =>
            context.Features.Get<IHttpRequestFeature>()!.RawTarget = rawTarget))
        { BaseAddress = new Uri("https://localhost") };
        AuthenticationHttpTests.SetCredentials(client, TestAccounts.Username("admin"), TestAccounts.AdminPassword);
        using var request = new HttpRequestMessage(method, rawTarget);
        return await client.SendAsync(request);
    }
}
