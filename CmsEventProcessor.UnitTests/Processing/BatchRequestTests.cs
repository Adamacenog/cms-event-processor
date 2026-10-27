using System.Text;
using CmsEventProcessor.Application.Processing.Models;
using static CmsEventProcessor.UnitTests.Entities.LifecycleFixtures;

namespace CmsEventProcessor.UnitTests.Processing;

/// <summary>
/// Verifies bounded, immutable processor inputs and exact request/key hashing independently of HTTP parsing.
/// </summary>
public sealed class BatchRequestTests
{
    /// <summary>
    /// Later changes to the caller's event array/body buffer cannot change an already constructed processing request.
    /// </summary>
    [Fact]
    public void InputOwnershipDoesNotRetainMutableBodyOrEventCollections()
    {
        var original = Event();
        var events = new[] { original };
        var body = Encoding.UTF8.GetBytes("synthetic exact bytes");
        var request = new BatchRequest("cms-test0001", events, body, "key");
        var digest = request.BodyHash.ToArray();
        events[0] = Event(version: 2, seconds: 1);
        Array.Clear(body);
        Assert.Same(original, request.Events[0]);
        Assert.True(request.BodyHash.Span.SequenceEqual(digest));
        Assert.Equal(32, request.BodyHash.Length);
        Assert.Equal(32, request.KeyHash.Length);
        Assert.DoesNotContain(typeof(BatchRequest).GetProperties(), property => property.Name is "Body" or "IdempotencyKey");
    }

    /// <summary>
    /// Null keys mean unkeyed work; otherwise key spelling and exact body bytes retain independent replay identities.
    /// </summary>
    [Fact]
    public void ExactKeyAndBodySpellingArePreservedInDigests()
    {
        var events = new[] { Event() };
        var body = Encoding.UTF8.GetBytes("[{}]");
        var first = new BatchRequest("cms-test0001", events, body, "key");
        var spacedKey = new BatchRequest("cms-test0001", events, body, "key ");
        var spacedBody = new BatchRequest("cms-test0001", events, Encoding.UTF8.GetBytes("[ {} ]"), "key");
        Assert.False(first.KeyHash.Span.SequenceEqual(spacedKey.KeyHash.Span));
        Assert.False(first.BodyHash.Span.SequenceEqual(spacedBody.BodyHash.Span));
        Assert.True(first.KeyHash.Span.SequenceEqual(spacedBody.KeyHash.Span));
        Assert.True(new BatchRequest("cms-test0001", events, body).KeyHash.IsEmpty);
    }

    /// <summary>
    /// Nonblank, bounded, control-free principal/key values are required without echoing rejected values.
    /// </summary>
    /// <param name="value">The invalid principal or explicitly supplied idempotency key.</param>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("value\ncontrol")]
    public void InvalidScopeOrKeyIsRejected(string value)
    {
        var events = new[] { Event() };
        var body = Encoding.UTF8.GetBytes("[]");
        Assert.Throws<ArgumentException>(() => new BatchRequest(value, events, body));
        Assert.Throws<ArgumentException>(() => new BatchRequest("cms-test0001", events, body, value));
    }

    /// <summary>
    /// Empty/oversized batches, null event entries, absent body bytes, and oversized identities/keys are rejected.
    /// </summary>
    [Fact]
    public void CollectionAndDigestInputsAreBounded()
    {
        var source = Event();
        var body = Encoding.UTF8.GetBytes("[]");
        Assert.Throws<ArgumentException>(() => new BatchRequest("cms-test0001", [], body));
        Assert.Throws<ArgumentException>(() => new BatchRequest("cms-test0001", Enumerable.Repeat(source, 101).ToArray(), body));
        Assert.Throws<ArgumentException>(() => new BatchRequest("cms-test0001", [null!], body));
        Assert.Throws<ArgumentException>(() => new BatchRequest("cms-test0001", [source], []));
        Assert.Throws<ArgumentException>(() => new BatchRequest(new string('a', 129), [source], body));
        Assert.Throws<ArgumentException>(() => new BatchRequest("cms-test0001", [source], body, new string('k', 129)));
        Assert.Equal(100, new BatchRequest("cms-test0001", Enumerable.Repeat(source, 100).ToArray(), body).Events.Count);
    }
}
