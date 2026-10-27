using System.Text;
using CmsEventProcessor.Api.Entities.Validation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

namespace CmsEventProcessor.UnitTests.Entities;

/// <summary>
/// Verifies consumer identifiers, role-bound canonical cursors, and strict paging without opening storage.
/// </summary>
public sealed class ConsumerInputTests
{
    /// <summary>
    /// Valid opaque IDs survive cursor encoding exactly, including Unicode, punctuation, and the maximum length.
    /// </summary>
    /// <param name="id">The opaque identifier.</param>
    /// <param name="administrator">The cursor visibility scope.</param>
    [Theory]
    [InlineData("Article-001", false)]
    [InlineData("a/b%2F?#+", false)]
    [InlineData("artigo-ç-😀", true)]
    [InlineData("字字字", false)]
    public void CursorPreservesSourceIdentityAndVisibility(string id, bool administrator)
    {
        var cursor = EntityCursor.Encode(id, administrator);
        Assert.True(EntityCursor.TryDecode(cursor, administrator, out var decoded));
        Assert.Equal(id, decoded);
        Assert.False(EntityCursor.TryDecode(cursor, !administrator, out _));
    }

    /// <summary>
    /// Malformed/unsupported cursors and invalid decoded IDs fail without replacement Unicode, exceptions, or permission changes.
    /// </summary>
    [Fact]
    public void InvalidAndNoncanonicalCursorsAreRejected()
    {
        foreach (var cursor in new[] { "", " ", "*", "A", new string('a', 521), "YWJj=", "YWJj+", "Mf9y", "MjpyOng", "MTpyOg", "MTpyOiB4" })
            Assert.False(EntityCursor.TryDecode(cursor, false, out _));
        var maximal = new string('字', 128);
        Assert.True(EntityCursor.TryDecode(EntityCursor.Encode(maximal, false), false, out var id));
        Assert.Equal(maximal, id);
        foreach (var invalid in new[] { "", " x", "x ", "x\n", "\ud800", new string('x', 129) })
            Assert.False(EntityInputValidator.IsValidId(invalid));
    }

    /// <summary>
    /// Defaults and documented page bounds parse, while empty, duplicate, unknown, overflow, signed, and malformed fields are safe errors.
    /// </summary>
    /// <param name="query">The synthetic query string.</param>
    /// <param name="valid">Whether the entire paging input should be accepted.</param>
    [Theory]
    [InlineData("", true)]
    [InlineData("?pageSize=1", true)]
    [InlineData("?pageSize=100", true)]
    [InlineData("?pageSize=0", false)]
    [InlineData("?pageSize=101", false)]
    [InlineData("?pageSize=", false)]
    [InlineData("?pageSize=1&pageSize=2", false)]
    [InlineData("?pageSize=2147483648", false)]
    [InlineData("?pageSize=%2B1", false)]
    [InlineData("?includeHidden=true", false)]
    [InlineData("?cursor=", false)]
    [InlineData("?cursor=abc&cursor=def", false)]
    public void PagingInputsAreStrictAndBounded(string query, bool valid)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query);
        var errors = EntityInputValidator.ValidatePage(context.Request.Query, false, out var size, out _);
        Assert.Equal(valid, errors.Count == 0);
        if (valid) Assert.InRange(size, 1, 100);
        if (query == "") Assert.Equal(20, size);
    }

    /// <summary>
    /// Original route segments are decoded once, distinguishing a slash from its literal percent spelling and preserving plus signs.
    /// </summary>
    /// <param name="raw">The original encoded request target.</param>
    /// <param name="expected">The exact source identifier.</param>
    /// <param name="disable">Whether this is the administrator route.</param>
    [Theory]
    [InlineData("/api/entities/a%2Fb", "a/b", false)]
    [InlineData("/api/entities/a%252Fb", "a%2Fb", false)]
    [InlineData("/api/entities/a%2Bb?cursor=ignored", "a+b", false)]
    [InlineData("/api/entities/a%3Fb/disable", "a?b", true)]
    [InlineData("/base/api/entities/a%23b/disable/", "a#b", true)]
    public void OriginalPathIsDecodedExactlyOnce(string raw, string expected, bool disable)
    {
        var context = new DefaultHttpContext();
        context.Features.Get<IHttpRequestFeature>()!.RawTarget = raw;
        Assert.Equal(expected, EntityInputValidator.ReadRouteId(context.Request, "unused", disable));
    }
}
