using System.Text.Json;

namespace CmsEventProcessor.Api.Entities.Dtos;

/// <summary>
/// Exposes the same current-snapshot shape to both consumer roles after server-side visibility filtering.
/// </summary>
/// <param name="Id">The exact source identifier.</param>
/// <param name="Payload">The preserved current JSON object.</param>
/// <param name="Version">The current CMS revision.</param>
/// <param name="CmsTimestamp">The source ordering watermark.</param>
/// <param name="CmsPublished">The CMS publication flag.</param>
/// <param name="AdminDisabled">The independent local override.</param>
internal sealed record EntityResponse(string Id, JsonElement Payload, long Version, DateTimeOffset CmsTimestamp,
    bool CmsPublished, bool AdminDisabled);
