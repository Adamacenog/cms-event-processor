using CmsEventProcessor.Application.Processing.Models;

namespace CmsEventProcessor.Api.Cms.Validation;

/// <summary>
/// Carries either a fully validated application request or sanitized boundary errors before storage is opened.
/// </summary>
/// <param name="StatusCode">The boundary HTTP status, or 200 for validated input.</param>
/// <param name="Request">The validated request, present only on success.</param>
/// <param name="Errors">Fixed field paths and safe messages, never submitted names or values.</param>
internal sealed record WebhookReadResult(int StatusCode, BatchRequest? Request = null,
    Dictionary<string, string[]>? Errors = null);
