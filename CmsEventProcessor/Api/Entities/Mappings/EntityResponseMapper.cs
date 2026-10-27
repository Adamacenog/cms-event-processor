using CmsEventProcessor.Api.Entities.Dtos;
using CmsEventProcessor.Application.Entities.Models;

namespace CmsEventProcessor.Api.Entities.Mappings;

/// <summary>
/// Shares the small snapshot-to-transport projection used by both list and lookup responses.
/// </summary>
internal static class EntityResponseMapper
{
    /// <summary>
    /// Projects selected application fields without exposing persistence metadata or changing content values.
    /// </summary>
    /// <param name="entity">The independently owned application snapshot.</param>
    /// <returns>The consumer response DTO.</returns>
    internal static EntityResponse Map(EntityDetails entity) => new(entity.Id, entity.Payload, entity.Version,
        entity.CmsTimestamp, entity.CmsPublished, entity.AdminDisabled);
}
