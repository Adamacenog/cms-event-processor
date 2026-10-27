using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CmsEventProcessor.Api.Cms.Dtos;

/// <summary>
/// Describes the strict CMS envelope; publish/unpublish require version and payload while delete may omit them.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class CmsEventInput
{
    [Required]
    public required string Type { get; init; }
    [Required, StringLength(128, MinimumLength = 1)]
    public required string Id { get; init; }
    [Required]
    public required string Timestamp { get; init; }
    public long? Version { get; init; }
    public JsonElement? Payload { get; init; }
}
