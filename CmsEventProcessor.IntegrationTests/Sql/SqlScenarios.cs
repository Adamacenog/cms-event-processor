using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CmsEventProcessor.Application.Processing.Models;
using CmsEventProcessor.Domain.Entities.Models;

namespace CmsEventProcessor.IntegrationTests.Sql;

/// <summary>
/// Builds validated synthetic batches and matching wire bytes without persisting test passwords or request bodies.
/// </summary>
internal static class SqlScenarios
{
    public static readonly DateTimeOffset Origin = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    internal static string RepositoryRoot { get; } = FindRepositoryRoot();

    /// <summary>
    /// Finds the checked-out solution from the test assembly without depending on private local planning files.
    /// </summary>
    /// <returns>The directory containing the solution and pinned tool manifest.</returns>
    /// <exception cref="InvalidOperationException">The test assembly is not inside a solution checkout.</exception>
    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CmsEventProcessor.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new InvalidOperationException("The solution checkout could not be located.");
    }

    /// <summary>
    /// Creates an immutable domain event with an independently owned synthetic object.
    /// </summary>
    /// <param name="id">The exact source identifier.</param>
    /// <param name="version">The positive content version, omitted for deletion.</param>
    /// <param name="seconds">The source-time offset.</param>
    /// <param name="type">The source lifecycle action.</param>
    /// <param name="json">The synthetic content object for publish/unpublish.</param>
    /// <returns>The validated domain event.</returns>
    /// <exception cref="JsonException">Synthetic content cannot be parsed.</exception>
    /// <exception cref="ArgumentException">The fixture violates domain input constraints.</exception>
    internal static CmsEvent Source(string id = "article-001", long version = 1, int seconds = 0,
        CmsEventType type = CmsEventType.Publish, string json = "{\"title\":\"Synthetic\"}")
    {
        using var document = JsonDocument.Parse(json);
        return new CmsEvent(type, id, Origin.AddSeconds(seconds), type == CmsEventType.Delete ? null : version,
            type == CmsEventType.Delete ? null : new EntityPayload(document.RootElement));
    }

    /// <summary>
    /// Hashes a synthetic batch's exact JSON bytes, optionally replacing only their textual representation for replay checks.
    /// </summary>
    /// <param name="events">Validated domain inputs in submitted order.</param>
    /// <param name="key">The optional private test key.</param>
    /// <param name="principal">The synthetic authenticated CMS scope.</param>
    /// <param name="body">Optional alternate exact bytes, used only for explicit body-mismatch scenarios.</param>
    /// <returns>An immutable request retaining digests rather than body bytes.</returns>
    /// <exception cref="ArgumentException">Fixture request invariants are violated.</exception>
    internal static BatchRequest Request(CmsEvent[] events, string? key = null, string principal = "cms-test0001", string? body = null)
    {
        var bytes = body is not null ? Encoding.UTF8.GetBytes(body) : Body(events);
        return new BatchRequest(principal, events, bytes, key);
    }

    /// <summary>
    /// Serializes valid source envelopes while omitting optional delete fields instead of emitting invalid null payloads.
    /// </summary>
    /// <param name="events">The validated source actions whose exact bytes are under test.</param>
    /// <returns>The synthetic wire representation used for body-digest comparisons.</returns>
    internal static byte[] Body(CmsEvent[] events) => JsonSerializer.SerializeToUtf8Bytes(
        events.Select(source => new
        {
            type = source.Type.ToString().ToLowerInvariant(),
            id = source.Id,
            timestamp = source.Timestamp,
            version = source.Version,
            payload = source.Payload?.Value
        }), new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
}
