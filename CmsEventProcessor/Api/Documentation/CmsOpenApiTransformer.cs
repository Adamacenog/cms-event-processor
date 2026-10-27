using CmsEventProcessor.Api.Cms;
using CmsEventProcessor.Api.Cms.Dtos;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace CmsEventProcessor.Api.Documentation;

/// <summary>
/// Adds exact-byte webhook input metadata and CMS Basic security to the document generated from implemented controller responses.
/// </summary>
internal sealed class CmsOpenApiTransformer : IOpenApiDocumentTransformer
{
    /// <summary>
    /// Describes bounded raw-body reading while deriving the envelope schema from the DTO actually used by validation.
    /// </summary>
    /// <param name="document">The generated document containing implemented operations only.</param>
    /// <param name="context">The framework schema-generation context.</param>
    /// <param name="cancellationToken">Cancellation of document generation.</param>
    /// <returns>A task completing after webhook request and authentication metadata are attached.</returns>
    /// <exception cref="OperationCanceledException">Schema generation is canceled.</exception>
    public async Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info = new OpenApiInfo
        {
            Title = "CMS Event Processor",
            Version = "v1",
            Description = "Implemented synchronous CMS ingestion. Consumer and health endpoints are not available yet."
        };
        document.Servers = [];
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
        {
            ["cmsBasic"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "basic",
                Description = "Preconfigured CMS organization credentials; use HTTPS outside loopback development."
            }
        };
        if (!document.Paths.TryGetValue("/cms/events", out var path) || path.Operations is null ||
            !path.Operations.TryGetValue(HttpMethod.Post, out var operation))
        {
            return;
        }
        operation.OperationId = "processCmsEvents";
        operation.Summary = "Validate and commit a CMS batch synchronously";
        operation.Description = "1-100 events; at most 1 MiB of UTF-8 JSON and depth 32. Validate all events before SQL. " +
            "Publish/unPublish require positive int64 version and object payload; delete may omit them. " +
            "Types are case-insensitive; envelope fields are case-sensitive and unknown fields/duplicate names are rejected. " +
            "Timestamps require seconds, Z/offset, and at most seven fractional digits. " +
            "A 200 reports committed outcomes; replay retains the original batch ID. A 503 around commit can be uncertain: retry the identical key/body.";
        operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("cmsBasic", document)] = [] }];
        var schema = await context.GetOrCreateSchemaAsync(typeof(CmsEventInput[]), null, cancellationToken);
        schema.MinItems = 1;
        schema.MaxItems = WebhookLimits.Events;
        operation.RequestBody = new OpenApiRequestBody
        {
            Required = true,
            Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() { Schema = schema } }
        };
        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "Idempotency-Key",
            In = ParameterLocation.Header,
            Description = "Optional single nonblank opaque value, max 128 characters, no controls. Case/whitespace preserved. " +
                "Scoped to CMS identity for seven days; identical exact bytes replay and different bytes conflict.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 128 }
        });
    }
}
