using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CmsEventProcessor.Api.Cms.Dtos;
using CmsEventProcessor.Application.Processing.Models;
using CmsEventProcessor.Domain.Entities.Models;

namespace CmsEventProcessor.Api.Cms.Validation;

/// <summary>
/// Collects expected envelope errors and constructs domain inputs only after the entire bounded document is valid.
/// </summary>
internal static class CmsBatchValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> EnvelopeNames = ["type", "id", "timestamp", "version", "payload"];

    /// <summary>
    /// Validates exact UTF-8 bytes, duplicate names, all event fields, and an optional opaque idempotency key.
    /// </summary>
    /// <param name="body">The exact bounded wire representation, hashed without reserialization.</param>
    /// <param name="principal">The authenticated CMS identity supplied by the server.</param>
    /// <param name="key">The single optional header value, preserved without normalization.</param>
    /// <returns>A validated request or fixed-path errors with no partially accepted events.</returns>
    public static WebhookReadResult Validate(ReadOnlyMemory<byte> body, string principal, string? key)
    {
        if (body.Length > WebhookLimits.BodyBytes)
        {
            return new WebhookReadResult(413);
        }
        if (key is not null && (string.IsNullOrWhiteSpace(key) || key.Length > 128 || key.Any(char.IsControl) || !IsUtf8(key)))
        {
            return Invalid("Idempotency-Key", "Supply one nonblank key of at most 128 characters without control characters.");
        }
        try
        {
            _ = StrictUtf8.GetCharCount(body.Span);
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = WebhookLimits.JsonDepth });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() is < 1 or > WebhookLimits.Events)
            {
                return Invalid("$", "Supply a JSON array containing 1-100 events.");
            }
            if (HasInvalidStructure(root))
            {
                return Invalid("$", "Duplicate JSON property names and invalid Unicode strings are not allowed.");
            }
            var errors = new Dictionary<string, string[]>();
            var events = new List<CmsEvent>();
            var index = 0;
            foreach (var element in root.EnumerateArray())
            {
                ValidateEvent(element, index++, errors);
            }
            if (errors.Count != 0)
            {
                return new WebhookReadResult(400, Errors: errors);
            }
            foreach (var element in root.EnumerateArray())
            {
                var input = element.Deserialize<CmsEventInput>(JsonOptions)!;
                _ = TryTimestamp(element.GetProperty("timestamp"), out var timestamp);
                events.Add(new CmsEvent(ParseType(input.Type), input.Id, timestamp, input.Version,
                    input.Payload is { } payload ? new EntityPayload(payload) : null));
            }
            return new WebhookReadResult(200, new BatchRequest(principal, events, body.Span, key));
        }
        catch (JsonException)
        {
            return Invalid("$", "Supply valid UTF-8 JSON with a maximum depth of 32.");
        }
        catch (DecoderFallbackException)
        {
            return Invalid("$", "Supply valid UTF-8 JSON.");
        }
    }

    /// <summary>
    /// Checks expected event errors using fixed field names, without using exceptions as field validation.
    /// </summary>
    /// <param name="element">One source envelope, possibly of the wrong JSON kind.</param>
    /// <param name="index">The zero-based source position.</param>
    /// <param name="errors">The bounded collection receiving safe field errors.</param>
    private static void ValidateEvent(JsonElement element, int index, Dictionary<string, string[]> errors)
    {
        var path = $"[{index}]";
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors[path] = ["Each event must be an object."];
            return;
        }
        if (element.EnumerateObject().Any(property => !EnvelopeNames.Contains(property.Name)))
        {
            errors[path] = ["Unknown envelope properties are not allowed."];
        }
        var type = ReadString(element, "type");
        var recognized = type is not null && (type.Equals("publish", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("unpublish", StringComparison.OrdinalIgnoreCase) || type.Equals("delete", StringComparison.OrdinalIgnoreCase));
        if (!recognized)
        {
            errors[$"{path}.type"] = ["Use publish, unPublish, or delete."];
        }
        var id = ReadString(element, "id");
        if (id is null || id.Length is < 1 or > 128 || id != id.Trim() || id.Any(char.IsControl))
        {
            errors[$"{path}.id"] = ["Supply an identifier of 1-128 characters without boundary whitespace or control characters."];
        }
        if (!element.TryGetProperty("timestamp", out var time) || !TryTimestamp(time, out _))
        {
            errors[$"{path}.timestamp"] = ["Use an ISO 8601 timestamp with seconds, an explicit Z or offset, and at most seven fractional digits."];
        }
        var delete = recognized && type!.Equals("delete", StringComparison.OrdinalIgnoreCase);
        if (element.TryGetProperty("version", out var version) ?
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt64(out var number) || number <= 0 : !delete)
        {
            errors[$"{path}.version"] = ["Supply a positive 64-bit integer; only delete may omit it."];
        }
        if (element.TryGetProperty("payload", out var payload) ? payload.ValueKind != JsonValueKind.Object : !delete)
        {
            errors[$"{path}.payload"] = ["Supply a JSON object; only delete may omit it."];
        }
    }

    /// <summary>
    /// Rejects ambiguous names and invalid escaped Unicode throughout the bounded document, including objects inside arrays.
    /// </summary>
    /// <param name="element">The current JSON value.</param>
    /// <returns>True when a name is repeated or a JSON string cannot be decoded without invalid surrogate escapes.</returns>
    private static bool HasInvalidStructure(JsonElement element)
    {
        try
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                _ = element.GetString();
            }
            else if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name) || HasInvalidStructure(property.Value)) return true;
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                return element.EnumerateArray().Any(HasInvalidStructure);
            }
            return false;
        }
        catch (InvalidOperationException)
        {
            // JsonDocument validates escapes lazily; only this JSON-inspection boundary classifies decoding failures.
            return true;
        }
    }

    /// <summary>
    /// Preserves source precision supported by .NET and SQL while rejecting zone-less or non-ISO timestamps.
    /// </summary>
    /// <param name="value">The supplied timestamp token.</param>
    /// <param name="timestamp">The parsed offset timestamp on success, otherwise its default value.</param>
    /// <returns>True for a valid explicitly zoned timestamp with at most seven fractional digits.</returns>
    private static bool TryTimestamp(JsonElement value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return text is { Length: >= 20 and <= 33 } && Regex.IsMatch(text,
            @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?(Z|[+-][0-9]{2}:[0-9]{2})$",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) && value.TryGetDateTimeOffset(out timestamp);
    }

    /// <summary>
    /// Reads only actual JSON strings without coercing numeric or null input.
    /// </summary>
    /// <param name="element">The validated object envelope.</param>
    /// <param name="name">The fixed known field name.</param>
    /// <returns>The string value, or null for a missing or non-string token.</returns>
    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// Converts an already checked action name without using permissive numeric enum parsing.
    /// </summary>
    /// <param name="type">The validated case-insensitive source action.</param>
    /// <returns>The corresponding domain action.</returns>
    private static CmsEventType ParseType(string type) => type.Equals("publish", StringComparison.OrdinalIgnoreCase) ?
        CmsEventType.Publish : type.Equals("delete", StringComparison.OrdinalIgnoreCase) ? CmsEventType.Delete : CmsEventType.Unpublish;

    /// <summary>
    /// Rejects invalid Unicode keys before their strict UTF-8 hashing can throw.
    /// </summary>
    /// <param name="value">The unmodified optional key.</param>
    /// <returns>True when the key can be represented without replacement characters.</returns>
    private static bool IsUtf8(string value)
    {
        try
        {
            _ = StrictUtf8.GetByteCount(value);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    /// Constructs one safe validation failure without echoing source input.
    /// </summary>
    /// <param name="field">A fixed contract path.</param>
    /// <param name="message">A fixed corrective message.</param>
    /// <returns>A 400 boundary result.</returns>
    private static WebhookReadResult Invalid(string field, string message) =>
        new(400, Errors: new Dictionary<string, string[]> { [field] = [message] });
}
