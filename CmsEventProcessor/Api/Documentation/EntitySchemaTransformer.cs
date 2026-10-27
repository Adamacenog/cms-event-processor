using CmsEventProcessor.Api.Entities.Dtos;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace CmsEventProcessor.Api.Documentation;

/// <summary>
/// Describes preserved object payloads and bounded pages in generated consumer response schemas.
/// </summary>
internal sealed class EntitySchemaTransformer : IOpenApiSchemaTransformer
{
    /// <summary>
    /// Refines the two transport response shapes without modifying application read models or runtime serialization.
    /// </summary>
    /// <param name="schema">The current generated response schema.</param>
    /// <param name="context">Metadata identifying the response DTO.</param>
    /// <param name="cancellationToken">The document-generation token.</param>
    /// <returns>A completed task after the matching schema is refined.</returns>
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.JsonTypeInfo.Type == typeof(EntityResponse))
        {
            schema.Properties!["payload"] = new OpenApiSchema { Type = JsonSchemaType.Object, AdditionalPropertiesAllowed = true };
            schema.Required = new HashSet<string> { "id", "payload", "version", "cmsTimestamp", "cmsPublished", "adminDisabled" };
        }
        else if (context.JsonTypeInfo.Type == typeof(EntityPageResponse))
        {
            if (schema.Properties!["items"] is OpenApiSchema items) items.MaxItems = 100;
            schema.Required = new HashSet<string> { "items", "nextCursor" };
        }
        return Task.CompletedTask;
    }
}
