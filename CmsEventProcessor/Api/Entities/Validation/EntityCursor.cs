using System.Text;

namespace CmsEventProcessor.Api.Entities.Validation;

/// <summary>
/// Encodes a bounded versioned continuation bound to reader/admin visibility; cursors grant no permissions and are not secrets.
/// </summary>
internal static class EntityCursor
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Encodes the last returned source ID using canonical unpadded base64url without applying locale-specific ordering.
    /// </summary>
    /// <param name="id">The valid last returned identifier.</param>
    /// <param name="includeHidden">The server-selected page visibility.</param>
    /// <returns>The versioned cursor intended for the same visibility on a later request.</returns>
    internal static string Encode(string id, bool includeHidden) =>
        Convert.ToBase64String(StrictUtf8.GetBytes((includeHidden ? "1:a:" : "1:r:") + id)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// Rejects oversized, noncanonical, invalid-Unicode, wrong-version, and cross-visibility cursor values.
    /// </summary>
    /// <param name="cursor">The untrusted query value.</param>
    /// <param name="includeHidden">The authenticated consumer's actual visibility.</param>
    /// <param name="id">The decoded valid identifier on success, otherwise null.</param>
    /// <returns>True only for a canonical supported cursor matching this visibility.</returns>
    internal static bool TryDecode(string? cursor, bool includeHidden, out string? id)
    {
        id = null;
        if (cursor is null || cursor.Length is < 1 or > 520 || cursor.Any(value => !char.IsAsciiLetterOrDigit(value) && value is not ('-' or '_')))
            return false;
        try
        {
            var encoded = cursor.Replace('-', '+').Replace('_', '/');
            var text = StrictUtf8.GetString(Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '=')));
            var prefix = includeHidden ? "1:a:" : "1:r:";
            if (!text.StartsWith(prefix, StringComparison.Ordinal) || !EntityInputValidator.IsValidId(text[4..])) return false;
            var candidate = text[4..];
            if (!string.Equals(cursor, Encode(candidate, includeHidden), StringComparison.Ordinal)) return false;
            id = candidate;
            return true;
        }
        catch (FormatException) { return false; }
        catch (DecoderFallbackException) { return false; }
    }
}
