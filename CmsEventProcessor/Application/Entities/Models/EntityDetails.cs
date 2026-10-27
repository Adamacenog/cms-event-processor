using System.Text.Json;

namespace CmsEventProcessor.Application.Entities.Models;

/// <summary>
/// Owns a current snapshot's selected read fields independently of its disposed database context and JSON document.
/// </summary>
/// <param name="id">The exact source identifier.</param>
/// <param name="payload">The unmodified JSON object, cloned before its backing document is disposed.</param>
/// <param name="version">The current CMS revision.</param>
/// <param name="cmsTimestamp">The source ordering watermark.</param>
/// <param name="cmsPublished">The CMS publication flag.</param>
/// <param name="adminDisabled">The independent local override.</param>
internal sealed class EntityDetails(string id, JsonElement payload, long version, DateTimeOffset cmsTimestamp,
    bool cmsPublished, bool adminDisabled)
{
    public string Id { get; } = id;
    public JsonElement Payload { get; } = payload.Clone();
    public long Version { get; } = version;
    public DateTimeOffset CmsTimestamp { get; } = cmsTimestamp;
    public bool CmsPublished { get; } = cmsPublished;
    public bool AdminDisabled { get; } = adminDisabled;
}
