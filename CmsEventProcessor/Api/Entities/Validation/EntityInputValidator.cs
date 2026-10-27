using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http.Features;

namespace CmsEventProcessor.Api.Entities.Validation;

/// <summary>
/// Validates fixed consumer query fields and exact source IDs without echoing supplied values or using entity exceptions.
/// </summary>
internal static class EntityInputValidator
{
    /// <summary>
    /// Rejects invalid source identifiers while preserving case, internal whitespace, punctuation, and valid Unicode.
    /// </summary>
    /// <param name="id">The decoded identifier.</param>
    /// <returns>True for one to 128 UTF-16 code units without boundary whitespace, controls, or invalid Unicode.</returns>
    internal static bool IsValidId(string? id)
    {
        if (id is null || id.Length is < 1 or > 128 || id != id.Trim() || id.Any(char.IsControl)) return false;
        try
        {
            _ = new UTF8Encoding(false, true).GetByteCount(id);
            return true;
        }
        catch (EncoderFallbackException) { return false; }
    }

    /// <summary>
    /// Parses only the two documented paging fields, rejecting duplicates and malformed or cross-visibility cursors.
    /// </summary>
    /// <param name="query">The request query collection.</param>
    /// <param name="includeHidden">The server-selected administrator visibility.</param>
    /// <param name="pageSize">The parsed count, defaulting to twenty.</param>
    /// <param name="afterId">The decoded continuation ID, or null.</param>
    /// <returns>Fixed-path errors; an empty dictionary means both output values are valid.</returns>
    internal static Dictionary<string, string[]> ValidatePage(IQueryCollection query, bool includeHidden, out int pageSize, out string? afterId)
    {
        pageSize = 20;
        afterId = null;
        var errors = new Dictionary<string, string[]>();
        if (query.Keys.Any(key => key is not ("pageSize" or "cursor"))) errors["query"] = ["Only pageSize and cursor are supported."];
        if (query.TryGetValue("pageSize", out var sizes) && (sizes.Count != 1 ||
            !int.TryParse(sizes[0], NumberStyles.None, CultureInfo.InvariantCulture, out pageSize) || pageSize is < 1 or > 100))
            errors["pageSize"] = ["Supply one integer from 1 to 100."];
        if (query.TryGetValue("cursor", out var cursors) && (cursors.Count != 1 || !EntityCursor.TryDecode(cursors[0], includeHidden, out afterId)))
            errors["cursor"] = ["Supply one valid cursor from a page with the same visibility."];
        return errors;
    }

    /// <summary>
    /// Decodes the original final path segment once, avoiding ASP.NET's retained escaped slash and percent double-decoding ambiguity.
    /// </summary>
    /// <param name="request">The request supplying the original raw target when available.</param>
    /// <param name="routeValue">The framework-bound value used only when the host provides no raw target.</param>
    /// <param name="disable">Whether the final literal disable segment must first be removed.</param>
    /// <returns>The exact once-decoded source identifier, ready for identifier validation.</returns>
    internal static string ReadRouteId(HttpRequest request, string routeValue, bool disable = false)
    {
        var raw = request.HttpContext.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (string.IsNullOrEmpty(raw)) return routeValue;
        var query = raw.IndexOf('?');
        var path = (query < 0 ? raw : raw[..query]).TrimEnd('/');
        if (disable) path = path[..^"/disable".Length];
        return Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]);
    }
}
