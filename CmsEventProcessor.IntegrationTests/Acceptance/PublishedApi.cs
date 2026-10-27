using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CmsEventProcessor.IntegrationTests.Sql;

namespace CmsEventProcessor.IntegrationTests.Acceptance;

/// <summary>
/// Publishes and starts the real API with explicit migrations and private generated accounts on an ephemeral loopback socket.
/// </summary>
/// <param name="host">The owned native Kestrel process.</param>
/// <param name="configuration">The hash/connection file removed on disposal.</param>
/// <param name="accounts">The newly generated test-only plaintext credentials held in memory.</param>
/// <param name="address">The actual ephemeral loopback listener.</param>
internal sealed class PublishedApi(AcceptanceProcess host, string configuration,
    IReadOnlyDictionary<string, AcceptanceAccount> accounts, Uri address) : IAsyncDisposable
{
    public IReadOnlyDictionary<string, AcceptanceAccount> Accounts => accounts;
    public Uri Address => address;
    public string Logs => host.Output + host.Error;

    /// <summary>
    /// Uses the documented Release publish, provisioning, and migration commands without a test server or developer configuration.
    /// </summary>
    /// <param name="connection">The private test-owned catalog connection; no existing user database is selected.</param>
    /// <returns>The started native host after authenticated readiness succeeds.</returns>
    /// <exception cref="InvalidOperationException">A command, generated account, or bounded host startup fails.</exception>
    /// <exception cref="IOException">Owned publish/configuration files cannot be written.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">A required executable cannot start.</exception>
    internal static async Task<PublishedApi> StartAsync(string connection)
    {
        var root = SqlScenarios.RepositoryRoot;
        var publish = Path.Combine(root, ".tmp", "acceptance-" + Guid.NewGuid().ToString("N"), "publish");
        Directory.CreateDirectory(publish);
        var configuration = Path.Combine(publish, "appsettings.Local.json");
        AcceptanceProcess? host = null;
        try
        {
            await AcceptanceProcess.RunAsync("dotnet", ["publish", Path.Combine(root, "CmsEventProcessor", "CmsEventProcessor.csproj"),
                "--configuration", "Release", "--no-build", "--no-restore", "--output", publish], root);
            if (Directory.EnumerateFiles(publish, "*Tests*", SearchOption.AllDirectories).Any() || File.Exists(configuration))
                throw new InvalidOperationException("The published application contained test assemblies or local configuration.");
            var dll = Path.Combine(publish, "CmsEventProcessor.dll");
            var output = await AcceptanceProcess.RunAsync("dotnet", [dll, "accounts", "init", "--file", configuration], publish);
            var accounts = AcceptanceAccount.Parse(output);
            var document = JsonNode.Parse(await File.ReadAllTextAsync(configuration))!.AsObject();
            if (accounts.Values.Any(account => document.ToJsonString().Contains(account.Password, StringComparison.Ordinal)))
                throw new InvalidOperationException("Provisioning persisted a plaintext test password.");
            document["Persistence"] = new JsonObject { ["WriteConnectionString"] = connection, ["ReadConnectionString"] = connection };
            document["Authentication"]!["AllowInsecureLoopbackHttp"] = true;
            await File.WriteAllTextAsync(configuration, document.ToJsonString());
            await AcceptanceProcess.RunAsync("dotnet", ["tool", "run", "dotnet-ef", "--", "database", "update", "--project",
                Path.Combine(root, "CmsEventProcessor"), "--context", "WriteContext", "--configuration", "Release", "--no-build",
                "--", "--configuration-directory", publish], root,
                new Dictionary<string, string> { ["CMS_MIGRATION_CONNECTION"] = connection });
            host = AcceptanceProcess.Start("dotnet", [dll, "--urls", "http://127.0.0.1:0"], publish);
            var address = await WaitForReadyAsync(host, accounts["Admin"]);
            return new PublishedApi(host, configuration, accounts, address);
        }
        catch
        {
            if (host is not null) await host.DisposeAsync();
            File.Delete(configuration);
            throw;
        }
    }

    /// <summary>
    /// Waits only for native host startup/readiness; committed business outcomes are never polled or delayed.
    /// </summary>
    /// <param name="host">The private native host whose listener is captured from its startup log.</param>
    /// <param name="admin">The generated administrator account used for readiness.</param>
    /// <returns>The actual loopback origin when the migrated database is ready.</returns>
    /// <exception cref="InvalidOperationException">The child exits or does not become ready within forty seconds.</exception>
    private static async Task<Uri> WaitForReadyAsync(AcceptanceProcess host, AcceptanceAccount admin)
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(admin.Username + ":" + admin.Password)));
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try
        {
            while (!host.HasExited)
            {
                var match = Regex.Match(host.Output, @"Now listening on: (http://127\.0\.0\.1:\d+)", RegexOptions.CultureInvariant);
                if (match.Success)
                {
                    var address = new Uri(match.Groups[1].Value);
                    try
                    {
                        using var ready = await client.GetAsync(new Uri(address, "/health/ready"), budget.Token);
                        if (ready.IsSuccessStatusCode && await ready.Content.ReadAsStringAsync(budget.Token) == "{\"status\":\"ready\"}") return address;
                    }
                    catch (HttpRequestException) { }
                    catch (OperationCanceledException) when (!budget.IsCancellationRequested) { }
                }
                await Task.Delay(100, budget.Token);
            }
        }
        catch (OperationCanceledException) { }
        throw new InvalidOperationException("The isolated native API did not become ready; private diagnostics were suppressed.");
    }

    /// <summary>
    /// Stops the test-owned host before removing its hash/connection file; nonsecret published binaries remain ignored locally.
    /// </summary>
    /// <returns>A value task completing after child cleanup and private configuration removal.</returns>
    /// <exception cref="IOException">The private configuration cannot be removed.</exception>
    public async ValueTask DisposeAsync()
    {
        try { await host.DisposeAsync(); }
        finally { File.Delete(configuration); }
    }
}
