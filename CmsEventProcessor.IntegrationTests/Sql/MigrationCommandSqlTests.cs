using System.Diagnostics;
using System.Text.Json;
using CmsEventProcessor.Infrastructure.Persistence.Contexts;
using Microsoft.EntityFrameworkCore;

namespace CmsEventProcessor.IntegrationTests.Sql;

/// <summary>
/// Verifies the documented EF migration command independently of authentication configuration and web startup.
/// </summary>
/// <param name="fixture">The shared SQL test server with disposable catalogs.</param>
[Collection("SQL")]
[Trait("Category", "Sql")]
public sealed class MigrationCommandSqlTests(SqlServerFixture fixture)
{
    /// <summary>
    /// The pinned CLI recreates an owned empty catalog from checked-in migrations without starting the HTTP host.
    /// </summary>
    /// <param name="useEnvironment">Whether the writer connection comes from a private environment override or local JSON.</param>
    /// <returns>A task completing after CLI exit, migration history, and schema freshness are verified.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DocumentedMigrationCommandWorksWithoutWebAccounts(bool useEnvironment)
    {
        await using var database = await fixture.CreateAsync();
        await using (var empty = await database.WriterAsync())
        {
            await empty.Database.EnsureDeletedAsync();
        }
        var privateDirectory = Path.Combine(SqlScenarios.RepositoryRoot, ".tmp", "migration-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(privateDirectory);
        var privateConfiguration = Path.Combine(privateDirectory, "appsettings.Local.json");
        if (!useEnvironment)
        {
            await File.WriteAllTextAsync(privateConfiguration, JsonSerializer.Serialize(new
            {
                Persistence = new { WriteConnectionString = database.ConnectionString }
            }));
        }
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = privateDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "tool", "run", "dotnet-ef", "--", "database", "update", "--project",
            Path.Combine(SqlScenarios.RepositoryRoot, "CmsEventProcessor"), "--context", nameof(WriteContext), "--configuration", "Release", "--no-build" })
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment.Remove("CMS_MIGRATION_CONNECTION");
        start.Environment.Remove("Persistence__WriteConnectionString");
        if (useEnvironment)
        {
            start.Environment["CMS_MIGRATION_CONNECTION"] = database.ConnectionString;
        }
        else
        {
            start.ArgumentList.Add("--");
            start.ArgumentList.Add("--configuration-directory");
            start.ArgumentList.Add(privateDirectory);
        }
        using var command = new Process { StartInfo = start };
        Assert.True(command.Start());
        var output = command.StandardOutput.ReadToEndAsync();
        var error = command.StandardError.ReadToEndAsync();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await command.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            command.Kill(entireProcessTree: true);
            throw;
        }
        var stdout = await output;
        var stderr = await error;
        if (File.Exists(privateConfiguration))
        {
            File.Delete(privateConfiguration);
        }
        Directory.Delete(privateDirectory);
        Assert.True(command.ExitCode == 0, "The explicit migration command failed; no private command output was published.");
        Assert.DoesNotContain("Password=", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password=", stderr, StringComparison.OrdinalIgnoreCase);
        await using var observer = await database.WriterAsync();
        Assert.Single(await observer.Database.GetAppliedMigrationsAsync());
        Assert.False(observer.Database.HasPendingModelChanges());
    }
}
