using System.Text.Json;
using CmsEventProcessor.Domain.Entities.Models;
using static CmsEventProcessor.UnitTests.Entities.LifecycleFixtures;

namespace CmsEventProcessor.UnitTests.Entities;

/// <summary>
/// Checks semantic JSON comparison, precise numbers, lifetime ownership, and preservation of confidential source values.
/// </summary>
public sealed class EntityPayloadTests
{
    /// <summary>
    /// Formatting, nested object order, equivalent numeric spelling, and equivalent escapes do not create false conflicts.
    /// </summary>
    /// <param name="left">The retained JSON object spelling.</param>
    /// <param name="right">The semantically equivalent incoming JSON object spelling.</param>
    [Theory]
    [InlineData("{\"a\":1,\"b\":true}", "{ \"b\": true, \"a\": 1 }")]
    [InlineData("{\"outer\":{\"a\":1,\"b\":[null,true,\"x\"]}}", "{\"outer\":{\"b\":[null,true,\"x\"],\"a\":1}}")]
    [InlineData("{\"a\":1}", "{\"a\":1.00e0}")]
    [InlineData("{\"a\":0.01}", "{\"a\":10e-3}")]
    [InlineData("{\"a\":-0}", "{\"a\":0.0}")]
    [InlineData("{\"a\":1e1000}", "{\"a\":10e999}")]
    [InlineData("{\"name\":\"a\"}", "{\"n\\u0061me\":\"\\u0061\"}")]
    public void EquivalentParsedValuesIgnoreOnlyInsignificantRepresentation(string left, string right)
    {
        Assert.True(Payload(left).HasSameContent(Payload(right)));
        Assert.True(Payload(right).HasSameContent(Payload(left)));
    }

    /// <summary>
    /// Array order, exact numbers beyond binary/decimal precision, property names, types, and string whitespace remain significant.
    /// </summary>
    /// <param name="left">The retained JSON object.</param>
    /// <param name="right">The genuinely different incoming object.</param>
    [Theory]
    [InlineData("{\"a\":[1,2]}", "{\"a\":[2,1]}")]
    [InlineData("{\"a\":[null]}", "{\"a\":[]}")]
    [InlineData("{\"a\":9007199254740992}", "{\"a\":9007199254740993}")]
    [InlineData("{\"a\":0.1234567890123456789012345678901}", "{\"a\":0.1234567890123456789012345678902}")]
    [InlineData("{\"a\":1}", "{\"a\":\"1\"}")]
    [InlineData("{\"a\":true}", "{\"a\":1}")]
    [InlineData("{\"a\":null}", "{}")]
    [InlineData("{\"Name\":\"x\"}", "{\"name\":\"x\"}")]
    [InlineData("{\"a\":\"text\"}", "{\"a\":\"Text\"}")]
    [InlineData("{\"a\":\" x \"}", "{\"a\":\"x\"}")]
    public void MeaningfulJsonDifferencesNeverCompareEqual(string left, string right)
    {
        Assert.False(Payload(left).HasSameContent(Payload(right)));
        Assert.False(Payload(right).HasSameContent(Payload(left)));
    }

    /// <summary>
    /// The owned payload survives document disposal without stripping HTML, rewriting numbers, or changing source property order.
    /// </summary>
    [Fact]
    public void OwnershipPreservesSourceContentAfterItsDocumentIsDisposed()
    {
        const string json = "{\"html\":\"<script>synthetic()</script>\",\"number\":1.00,\"nested\":{\"z\":1,\"a\":2}}";
        EntityPayload payload;
        using (var document = JsonDocument.Parse(json))
        {
            payload = new EntityPayload(document.RootElement);
        }
        Assert.Equal(json, payload.Value.GetRawText());
        Assert.Equal("<script>synthetic()</script>", payload.Value.GetProperty("html").GetString());
        Assert.False(payload.HasSameContent(null));
    }

    /// <summary>
    /// Duplicate names are rejected at the root, in nested objects, and inside arrays, including escaped equivalent names.
    /// </summary>
    /// <param name="json">The ambiguous JSON payload that must not enter retained domain state.</param>
    [Theory]
    [InlineData("{\"secret\":\"first\",\"secret\":\"second\"}")]
    [InlineData("{\"a\":1,\"\\u0061\":2}")]
    [InlineData("{\"nested\":{\"a\":1,\"a\":2}}")]
    [InlineData("{\"items\":[{\"a\":1,\"a\":2}]}")]
    public void DuplicatePropertyNamesAreRejectedWithoutEchoingContent(string json)
    {
        var exception = Assert.Throws<ArgumentException>(() => Payload(json));
        Assert.DoesNotContain(json, exception.Message);
        Assert.DoesNotContain("first", exception.Message);
        Assert.DoesNotContain("second", exception.Message);
    }

    /// <summary>
    /// Ordinal property comparison permits differently cased names while still comparing them as distinct properties.
    /// </summary>
    [Fact]
    public void DifferentlyCasedPropertyNamesAreNotDuplicates()
    {
        Assert.Equal(2, Payload("{\"a\":1,\"A\":2}").Value.EnumerateObject().Count());
    }

    /// <summary>
    /// Payload invariants reject scalar/array roots and uninitialized JSON values instead of admitting non-object content.
    /// </summary>
    /// <param name="json">A valid JSON value with an unsupported root kind.</param>
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("\"text\"")]
    public void PayloadMustBeAnObject(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<ArgumentException>(() => new EntityPayload(document.RootElement));
        Assert.Throws<ArgumentException>(() => new EntityPayload(default));
    }
}
