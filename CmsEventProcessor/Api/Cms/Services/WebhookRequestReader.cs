using CmsEventProcessor.Api.Cms.Validation;
using Microsoft.Net.Http.Headers;

namespace CmsEventProcessor.Api.Cms.Services;

/// <summary>
/// Bounds body reads independently of Content-Length and preserves exact UTF-8 bytes for replay hashing.
/// </summary>
internal sealed class WebhookRequestReader
{
    /// <summary>
    /// Reads and validates an authenticated JSON batch without opening storage or buffering an unbounded stream.
    /// </summary>
    /// <param name="request">The authenticated request carrying the body and optional single key.</param>
    /// <param name="principal">The server-authenticated CMS principal.</param>
    /// <param name="cancellationToken">The request-abort token used for every stream read.</param>
    /// <returns>A fully validated batch or sanitized 400, 413, or 415 boundary result.</returns>
    /// <exception cref="OperationCanceledException">The caller cancels body reading.</exception>
    /// <exception cref="IOException">The request transport cannot be read.</exception>
    public async Task<WebhookReadResult> ReadAsync(HttpRequest request, string principal, CancellationToken cancellationToken)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType) ||
            !contentType.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
            contentType.Charset.HasValue && !HeaderUtilities.RemoveQuotes(contentType.Charset).Equals("utf-8", StringComparison.OrdinalIgnoreCase) ||
            request.Headers.ContentEncoding.Any(value => !string.Equals(value, "identity", StringComparison.OrdinalIgnoreCase)))
        {
            return new WebhookReadResult(415);
        }
        if (request.ContentLength > WebhookLimits.BodyBytes)
        {
            return new WebhookReadResult(413);
        }
        var keys = request.Headers["Idempotency-Key"];
        if (keys.Count > 1)
        {
            return new WebhookReadResult(400, Errors: new() { ["Idempotency-Key"] = ["Supply at most one key header value."] });
        }
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await request.Body.ReadAsync(buffer.AsMemory(0,
            Math.Min(buffer.Length, WebhookLimits.BodyBytes + 1 - (int)body.Length)), cancellationToken)) != 0)
        {
            body.Write(buffer, 0, count);
            if (body.Length > WebhookLimits.BodyBytes)
            {
                return new WebhookReadResult(413);
            }
        }
        return CmsBatchValidator.Validate(body.ToArray(), principal, keys.Count == 0 ? null : keys[0]);
    }
}
