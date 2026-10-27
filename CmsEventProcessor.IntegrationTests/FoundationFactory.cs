using CmsEventProcessor;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CmsEventProcessor.IntegrationTests;

/// <summary>
/// Hosts the production pipeline with isolated credentials and optional test-only controllers.
/// </summary>
internal sealed class FoundationFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// Reload is disabled so rotation tests can verify that credentials change only after host restart.
    /// </summary>
    public string? ConfigurationFile { get; init; }
    public bool MissingAccounts { get; init; }
    public bool AllowLoopbackHttp { get; init; }
    public string RemoteAddress { get; init; } = "127.0.0.1";
    public bool IncludeTestControllers { get; init; } = true;
    public Dictionary<string, string?> Settings { get; init; } = new();
    public Action<IServiceCollection>? ConfigureTestServices { get; init; }
    public CaptureLogger Logs { get; } = new();

    /// <summary>
    /// Replaces developer configuration and installs test routes, peer simulation, and log capture.
    /// </summary>
    /// <param name="builder">The test host builder receiving isolated configuration, probe controllers, and diagnostic services.</param>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            // Tests must never consume a developer's real accounts or local secrets.
            configuration.Sources.Clear();
            if (ConfigurationFile is not null)
            {
                configuration.AddJsonFile(ConfigurationFile, optional: false, reloadOnChange: false);
            }
            else if (!MissingAccounts)
            {
                configuration.AddInMemoryCollection(TestAccounts.Configuration);
            }
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:AllowInsecureLoopbackHttp"] = AllowLoopbackHttp.ToString(),
                ["Persistence:WriteConnectionString"] = "Server=127.0.0.1;Database=FoundationProbe;Integrated Security=true;TrustServerCertificate=true",
                ["Persistence:ReadConnectionString"] = "Server=127.0.0.1;Database=FoundationProbe;Integrated Security=true;TrustServerCertificate=true"
            });
            configuration.AddInMemoryCollection(Settings);
        });
        builder.ConfigureServices(services =>
        {
            if (IncludeTestControllers)
            {
                services.AddControllers().AddApplicationPart(typeof(ProbeController).Assembly);
            }
            services.AddSingleton<IStartupFilter>(new RemoteAddressFilter(RemoteAddress));
            services.AddLogging(logging => logging.AddProvider(Logs));
            services.Configure<LoggerFilterOptions>(options =>
            {
                options.MinLevel = LogLevel.Trace;
                options.Rules.Clear();
                options.Rules.Add(new LoggerFilterRule(null, "Microsoft.EntityFrameworkCore", LogLevel.None, null));
            });
            ConfigureTestServices?.Invoke(services);
        });
    }

    /// <summary>
    /// Creates a client with an explicit transport scheme and no redirect-following behavior.
    /// </summary>
    /// <param name="scheme">The simulated request transport, normally https or explicitly http for transport tests.</param>
    /// <returns>An HTTP client backed by the test server with automatic redirects disabled.</returns>
    /// <exception cref="Microsoft.Extensions.Options.OptionsValidationException">The test deliberately supplies missing or invalid account configuration.</exception>
    /// <exception cref="System.InvalidOperationException">The test application cannot be initialized.</exception>
    public HttpClient Client(string scheme = "https") => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri($"{scheme}://localhost"),
        AllowAutoRedirect = false
    });

}
