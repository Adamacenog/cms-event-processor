using System.Text;
using CmsEventProcessor.Api.Cms;
using CmsEventProcessor.Api.Cms.Validation;
using CmsEventProcessor.Domain.Entities.Models;

namespace CmsEventProcessor.UnitTests.Webhook;

/// <summary>
/// Verifies successful input boundaries, exact hashing, precision, and defensive validation beyond normal HTTP header shapes.
/// </summary>
public sealed class CmsBatchValidatorTests
{
    private const string Body = """
        [{"type":"publish","id":"Article-001","version":1,"timestamp":"2026-01-01T12:00:00Z","payload":{"html":"<script>synthetic</script>","number":9007199254740993}}]
        """;

    /// <summary>
    /// Valid content remains unmodified at the exact body-size boundary, with case-sensitive IDs and byte-based replay hashing.
    /// </summary>
    [Fact]
    public void MaximumBodyAndArbitraryContentArePreserved()
    {
        var bytes = Encoding.UTF8.GetBytes(Body.PadRight(WebhookLimits.BodyBytes));
        var result = CmsBatchValidator.Validate(bytes, "cms-test0001", "opaque key ");
        Assert.Equal(200, result.StatusCode);
        var source = Assert.Single(result.Request!.Events);
        Assert.Equal("Article-001", source.Id);
        Assert.Equal("<script>synthetic</script>", source.Payload!.Value.GetProperty("html").GetString());
        Assert.Equal(9007199254740993, source.Payload.Value.GetProperty("number").GetInt64());
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(bytes), result.Request.BodyHash.ToArray());
        Assert.Equal(413, CmsBatchValidator.Validate(Encoding.UTF8.GetBytes(Body.PadRight(WebhookLimits.BodyBytes + 1)), "cms-test0001", null).StatusCode);
    }

    /// <summary>
    /// Recognized actions are case-insensitive; optional delete content is validated and explicitly zoned instants normalize to UTC.
    /// </summary>
    /// <param name="type">The recognized mixed-case action.</param>
    /// <param name="zone">The source zone or offset.</param>
    [Theory]
    [InlineData("PuBlIsH", "Z")]
    [InlineData("UNPUBLISH", "+03:00")]
    [InlineData("DELETE", "-03:00")]
    public void RecognizedTypesAndExplicitOffsetsAreAccepted(string type, string zone)
    {
        var bytes = Encoding.UTF8.GetBytes(Body.Replace("publish", type).Replace("12:00:00Z", "12:00:00.1234567" + zone));
        var result = CmsBatchValidator.Validate(bytes, "cms-test0001", null);
        Assert.Equal(200, result.StatusCode);
        var source = Assert.Single(result.Request!.Events);
        Assert.Equal(TimeSpan.Zero, source.Timestamp.Offset);
        Assert.Equal(1_234_567, source.Timestamp.Ticks % TimeSpan.TicksPerSecond);
    }

    /// <summary>
    /// Delete accepts omitted optional fields but rejects explicit null/non-object payloads and invalid revisions.
    /// </summary>
    /// <param name="extra">Optional synthetic delete fields.</param>
    /// <param name="status">The expected validation result.</param>
    [Theory]
    [InlineData("", 200)]
    [InlineData(",\"version\":1,\"payload\":{}", 200)]
    [InlineData(",\"version\":null", 400)]
    [InlineData(",\"version\":0", 400)]
    [InlineData(",\"payload\":null", 400)]
    [InlineData(",\"payload\":[]", 400)]
    public void DeleteOptionalFieldsAreValidatedWhenPresent(string extra, int status)
    {
        var bytes = Encoding.UTF8.GetBytes("[{\"type\":\"delete\",\"id\":\"X\",\"timestamp\":\"2026-01-01T00:00:00Z\"" + extra + "}]");
        Assert.Equal(status, CmsBatchValidator.Validate(bytes, "cms-test0001", null).StatusCode);
    }

    /// <summary>
    /// Exactly 100 events and JSON depth 32 are accepted; the next depth is rejected before constructing domain events.
    /// </summary>
    [Fact]
    public void CountAndDepthBoundariesAreInclusive()
    {
        var hundred = "[" + string.Join(',', Enumerable.Repeat(Body[1..^1], 100)) + "]";
        Assert.Equal(100, CmsBatchValidator.Validate(Encoding.UTF8.GetBytes(hundred), "cms-test0001", null).Request!.Events.Count);
        // Root array, envelope, and payload consume three nesting levels; 29 nested arrays reach exactly 32.
        var atLimit = Body.Replace("9007199254740993", new string('[', 29) + "0" + new string(']', 29));
        Assert.Equal(200, CmsBatchValidator.Validate(Encoding.UTF8.GetBytes(atLimit), "cms-test0001", null).StatusCode);
        var overLimit = Body.Replace("9007199254740993", new string('[', 30) + "0" + new string(']', 30));
        Assert.Equal(400, CmsBatchValidator.Validate(Encoding.UTF8.GetBytes(overLimit), "cms-test0001", null).StatusCode);
    }

    /// <summary>
    /// Invalid Unicode/control keys return expected validation results instead of throwing during strict UTF-8 hashing.
    /// </summary>
    [Fact]
    public void InvalidOpaqueKeysAreSafeValidationFailures()
    {
        foreach (var key in new[] { "\ud800", "control\u0001", "", " ", new string('x', 129) })
        {
            var result = CmsBatchValidator.Validate(Encoding.UTF8.GetBytes(Body), "cms-test0001", key);
            Assert.Equal(400, result.StatusCode);
            Assert.True(result.Errors!.ContainsKey("Idempotency-Key"));
        }
    }

    /// <summary>
    /// Non-UTF-8 source bytes are rejected without replacing confidential content or throwing an unexpected error.
    /// </summary>
    [Fact]
    public void InvalidUtf8IsRejected()
    {
        var bytes = Encoding.UTF8.GetBytes(Body);
        bytes[Array.IndexOf(bytes, (byte)'A')] = 0xff;
        Assert.Equal(400, CmsBatchValidator.Validate(bytes, "cms-test0001", null).StatusCode);
    }
}
