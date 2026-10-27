using System.Text.Json;
using CmsEventProcessor.Domain.Entities.Models;

namespace CmsEventProcessor.UnitTests.Entities;

/// <summary>
/// Supplies owned synthetic content and deterministic source times without storage, HTTP, or wall-clock dependencies.
/// </summary>
internal static class LifecycleFixtures
{
    public const string Id = "article-001";
    public const string Content = "{\"title\":\"Example\",\"count\":1}";
    public static readonly DateTimeOffset Origin = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Parses synthetic content into a payload that survives disposal of its source document.
    /// </summary>
    /// <param name="json">The JSON object to use as synthetic CMS content.</param>
    /// <returns>An independently owned domain payload preserving the parsed values.</returns>
    /// <exception cref="JsonException">The supplied fixture JSON cannot be parsed.</exception>
    /// <exception cref="ArgumentException">The supplied fixture is not an unambiguous JSON object.</exception>
    public static EntityPayload Payload(string json = Content)
    {
        using var document = JsonDocument.Parse(json);
        return new EntityPayload(document.RootElement);
    }

    /// <summary>
    /// Creates a validated source action relative to a fixed origin, omitting content/revision for deletes.
    /// </summary>
    /// <param name="type">The publish, unpublish, or delete action under test.</param>
    /// <param name="version">The content revision for publish/unpublish; deletes omit it.</param>
    /// <param name="seconds">The source-time offset relative to the deterministic origin.</param>
    /// <param name="json">The publish/unpublish content; deletes omit it.</param>
    /// <param name="id">The case-sensitive identifier, preserved without normalization.</param>
    /// <returns>The validated event for a lifecycle scenario.</returns>
    /// <exception cref="JsonException">The fixture content cannot be parsed.</exception>
    /// <exception cref="ArgumentException">A fixture violates event, identifier, revision, or content invariants.</exception>
    public static CmsEvent Event(CmsEventType type = CmsEventType.Publish, long version = 1,
        int seconds = 0, string json = Content, string id = Id) =>
        new(type, id, Origin.AddSeconds(seconds), type == CmsEventType.Delete ? null : version,
            type == CmsEventType.Delete ? null : Payload(json));

    /// <summary>
    /// Creates a retained snapshot with independently controlled publication and administrator flags.
    /// </summary>
    /// <param name="version">The retained source revision.</param>
    /// <param name="seconds">The last accepted CMS time relative to the fixed origin.</param>
    /// <param name="published">Whether the CMS publishes this snapshot.</param>
    /// <param name="disabled">Whether a local administrator hides this lifecycle.</param>
    /// <param name="json">The retained synthetic content.</param>
    /// <param name="id">The exact identifier for state-ownership scenarios.</param>
    /// <returns>An immutable snapshot for transition and visibility checks.</returns>
    /// <exception cref="JsonException">The fixture content cannot be parsed.</exception>
    /// <exception cref="ArgumentException">A fixture violates snapshot, identifier, revision, or content invariants.</exception>
    public static EntitySnapshot Snapshot(long version = 1, int seconds = 0,
        bool published = true, bool disabled = false, string json = Content, string id = Id) =>
        new(id, version, Payload(json), Origin.AddSeconds(seconds), published, disabled);
}
