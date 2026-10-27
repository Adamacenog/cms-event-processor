using CmsEventProcessor.Api.Cms.Dtos;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace CmsEventProcessor.Api.Documentation;

/// <summary>
/// Refines the generated transport schema with the same strict envelope and conditional content rules used at runtime.
/// </summary>
internal sealed class CmsEventSchemaTransformer : IOpenApiSchemaTransformer
{
    /// <summary>
    /// Restricts envelope names and payload kinds while retaining arbitrary content properties and case-insensitive actions.
    /// </summary>
    /// <param name="schema">The generated schema for the current CLR type.</param>
    /// <param name="context">Metadata identifying the actual input DTO.</param>
    /// <param name="cancellationToken">The document-generation token.</param>
    /// <returns>A completed task after the matching input schema is refined.</returns>
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.JsonTypeInfo.Type != typeof(CmsEventInput)) return Task.CompletedTask;
        schema.AdditionalPropertiesAllowed = false;
        schema.Required = new HashSet<string> { "type", "id", "timestamp" };
        schema.Properties!["payload"] = new OpenApiSchema { Type = JsonSchemaType.Object, AdditionalPropertiesAllowed = true };
        schema.Properties["version"] = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int64", Minimum = "1" };
        schema.Properties["timestamp"] = new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            Format = "date-time",
            Pattern = @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?(Z|[+-][0-9]{2}:[0-9]{2})$"
        };
        schema.OneOf =
        [
            new OpenApiSchema { Properties = new Dictionary<string, IOpenApiSchema>
                { ["type"] = new OpenApiSchema { Type = JsonSchemaType.String, Pattern = "^([pP][uU][bB][lL][iI][sS][hH]|[uU][nN][pP][uU][bB][lL][iI][sS][hH])$" } },
                Required = new HashSet<string> { "version", "payload" } },
            new OpenApiSchema { Properties = new Dictionary<string, IOpenApiSchema>
                { ["type"] = new OpenApiSchema { Type = JsonSchemaType.String, Pattern = "^[dD][eE][lL][eE][tT][eE]$" } } }
        ];
        return Task.CompletedTask;
    }
}
