namespace CmsEventProcessor.Api.Entities.Dtos;

/// <summary>
/// Returns a bounded page and an opaque continuation only when another visible row exists.
/// </summary>
/// <param name="Items">The current snapshots visible to the authenticated consumer.</param>
/// <param name="NextCursor">The visibility-bound continuation, or null at the end.</param>
internal sealed record EntityPageResponse(IReadOnlyList<EntityResponse> Items, string? NextCursor);
