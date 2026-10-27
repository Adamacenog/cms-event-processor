using CmsEventProcessor.Application.Processing.Options;
using CmsEventProcessor.Infrastructure.Persistence.Contexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace CmsEventProcessor.Infrastructure.Persistence;

/// <summary>
/// Supports explicit migration tooling without booting the web host or requiring authentication accounts.
/// </summary>
internal sealed class WriteContextDesignFactory : IDesignTimeDbContextFactory<WriteContext>
{
    /// <summary>
    /// Uses an explicit migration connection or the same private writer configuration as the application.
    /// </summary>
    /// <param name="args">An optional --configuration-directory and private folder, forwarded after the EF command's separator.</param>
    /// <returns>The design-time writer context.</returns>
    /// <exception cref="ArgumentException">An explicitly supplied connection string is invalid.</exception>
    /// <exception cref="InvalidDataException">A required local configuration file contains malformed JSON.</exception>
    public WriteContext CreateDbContext(string[] args)
    {
        var directory = Directory.GetCurrentDirectory();
        if (args.Length > 0)
        {
            if (args.Length != 2 || args[0] != "--configuration-directory" || string.IsNullOrWhiteSpace(args[1]))
            {
                throw new ArgumentException("Unsupported migration configuration arguments.", nameof(args));
            }
            directory = Path.GetFullPath(args[1]);
        }
        var connection = Environment.GetEnvironmentVariable("CMS_MIGRATION_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
        {
            var configuration = new ConfigurationBuilder().SetBasePath(directory)
                .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
                .AddJsonFile("CmsEventProcessor/appsettings.Local.json", optional: true, reloadOnChange: false)
                .AddEnvironmentVariables().Build();
            connection = configuration["Persistence:WriteConnectionString"] ??
                "Server=127.0.0.1;Database=CmsEventProcessor;Integrated Security=true;TrustServerCertificate=true";
        }
        var options = new DbContextOptionsBuilder<WriteContext>();
        SqlConfiguration.Configure(options, connection, new ProcessingOptions());
        return new WriteContext(options.Options);
    }
}
