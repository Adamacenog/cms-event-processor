namespace CmsEventProcessor.Application.Entities.Models;

/// <summary>
/// Carries a bounded immutable page without exposing a database query, total count, or encoded HTTP cursor.
/// </summary>
/// <param name="items">The selected visible rows, copied to prevent collection mutation.</param>
/// <param name="nextId">The last returned identifier when an additional matching row exists, otherwise null.</param>
internal sealed class EntityPage(IReadOnlyList<EntityDetails> items, string? nextId)
{
    public IReadOnlyList<EntityDetails> Items { get; } = Array.AsReadOnly(items.ToArray());
    public string? NextId { get; } = nextId;
}
