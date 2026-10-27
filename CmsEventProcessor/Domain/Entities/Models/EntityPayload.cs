using System.Text.Json;

namespace CmsEventProcessor.Domain.Entities.Models;

/// <summary>
/// Owns an unmodified JSON object and compares its values without textual or floating-point normalization.
/// </summary>
internal sealed class EntityPayload
{
    /// <summary>
    /// Rejects non-object or ambiguous content and takes ownership independent of the caller's JSON document.
    /// </summary>
    /// <param name="value">The parsed JSON object whose original values and spelling must be retained.</param>
    /// <exception cref="ArgumentException">The value is not an object or contains repeated property names at any depth.</exception>
    /// <exception cref="ObjectDisposedException">The document supplying the value has already been disposed.</exception>
    public EntityPayload(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Entity payloads must be JSON objects.", nameof(value));
        }

        ValidateUniqueProperties(value);
        Value = value.Clone();
    }

    /// <summary>
    /// Remains usable after the input document is disposed; its confidential content must never be logged.
    /// </summary>
    public JsonElement Value { get; }

    /// <summary>
    /// Compares JSON values recursively, ignoring object-property order and insignificant number formatting.
    /// </summary>
    /// <param name="other">The owned payload to compare, or null when no comparable content exists.</param>
    /// <returns>True for equal parsed values; arrays remain ordered and numeric comparison performs no rounding.</returns>
    public bool HasSameContent(EntityPayload? other) =>
        other is not null && JsonElement.DeepEquals(Value, other.Value);

    /// <summary>
    /// Rejects repeated property names, including escaped equivalents, without disclosing their names or values.
    /// </summary>
    /// <param name="value">The root object to inspect, including objects nested inside arrays.</param>
    /// <exception cref="ArgumentException">An object contains duplicate ordinal property names.</exception>
    private static void ValidateUniqueProperties(JsonElement value)
    {
        var pending = new Stack<JsonElement>();
        pending.Push(value);
        while (pending.TryPop(out var current))
        {
            if (current.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in current.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                    {
                        throw new ArgumentException("Entity payloads must not contain duplicate property names.", nameof(value));
                    }
                    pending.Push(property.Value);
                }
            }
            else if (current.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in current.EnumerateArray())
                {
                    pending.Push(element);
                }
            }
        }
    }
}
