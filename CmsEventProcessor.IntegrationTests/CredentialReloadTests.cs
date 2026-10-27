using System.Net;
using CmsEventProcessor.Application.Identity.Models;
using CmsEventProcessor.Infrastructure.Identity.Services;

namespace CmsEventProcessor.IntegrationTests;

/// <summary>
/// Verifies explicit credential rotation against running and restarted application instances.
/// </summary>
public sealed class CredentialReloadTests
{
    /// <summary>
    /// Starting the host preserves its account file. Rotation leaves the running host on its old snapshot;
    /// a restarted host accepts the new password and rejects the old one.
    /// </summary>
    /// <returns>A task that completes after old and new passwords are checked against both the running and restarted hosts.</returns>
    [Fact]
    public async Task ExplicitRotationTakesEffectOnlyAfterHostRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cms-reload-tests-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "appsettings.Local.json");
        try
        {
            var files = new CredentialFile(new PasswordHashService());
            var reader = files.Initialize(path).Single(item => item.Account.Kind == AccountKind.Reader);
            var initialBytes = File.ReadAllBytes(path);
            await using var originalHost = new FoundationFactory { ConfigurationFile = path };
            using var client = originalHost.Client();
            AuthenticationHttpTests.SetCredentials(client, reader.Account.Username, reader.Password);
            using var before = await client.GetAsync("/_test/consumer");
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
            Assert.Equal(initialBytes, File.ReadAllBytes(path));

            var rotated = files.Rotate(path, reader.Account.Username);
            using var stillOriginal = await client.GetAsync("/_test/consumer");
            Assert.Equal(HttpStatusCode.OK, stillOriginal.StatusCode);
            AuthenticationHttpTests.SetCredentials(client, reader.Account.Username, rotated.Password);
            using var notLoadedYet = await client.GetAsync("/_test/consumer");
            Assert.Equal(HttpStatusCode.Unauthorized, notLoadedYet.StatusCode);

            await using var restartedHost = new FoundationFactory { ConfigurationFile = path };
            using var restartedClient = restartedHost.Client();
            AuthenticationHttpTests.SetCredentials(restartedClient, reader.Account.Username, rotated.Password);
            using var loaded = await restartedClient.GetAsync("/_test/consumer");
            Assert.Equal(HttpStatusCode.OK, loaded.StatusCode);
            AuthenticationHttpTests.SetCredentials(restartedClient, reader.Account.Username, reader.Password);
            using var revoked = await restartedClient.GetAsync("/_test/consumer");
            Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        }
        finally
        {
            File.Delete(path);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory);
            }
        }
    }
}
