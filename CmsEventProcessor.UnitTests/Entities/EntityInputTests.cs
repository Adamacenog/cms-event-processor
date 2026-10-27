using CmsEventProcessor.Domain.Entities.Models;
using CmsEventProcessor.Domain.Entities.Services;
using static CmsEventProcessor.UnitTests.Entities.LifecycleFixtures;

namespace CmsEventProcessor.UnitTests.Entities;

/// <summary>
/// Verifies invariant validation and exact identifier/time ownership at the domain boundary, independently of future HTTP binding.
/// </summary>
public sealed class EntityInputTests
{
    /// <summary>
    /// Events, snapshots, and deletion markers reject missing, empty, padded, or control-containing identifiers without normalizing them.
    /// </summary>
    /// <param name="id">The invalid opaque identifier supplied to each state boundary.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" padded")]
    [InlineData("padded ")]
    [InlineData("middle\ncontrol")]
    [InlineData("\0")]
    public void InvalidIdentifiersCannotEnterEventsOrRetainedState(string? id)
    {
        Assert.ThrowsAny<ArgumentException>(() => new CmsEvent(CmsEventType.Delete, id!, Origin));
        Assert.ThrowsAny<ArgumentException>(() => Snapshot(id: id!));
        Assert.ThrowsAny<ArgumentException>(() => new DeletionMarker(id!, Origin));
    }

    /// <summary>
    /// Identifiers preserve case, internal spaces, non-ASCII text, and opaque path characters within the bounded format.
    /// </summary>
    /// <param name="id">A valid opaque identifier whose exact value must survive all domain models.</param>
    [Theory]
    [InlineData("a")]
    [InlineData("CaseSensitive")]
    [InlineData("internal space")]
    [InlineData("article-😀")]
    [InlineData("opaque/path?query")]
    public void ValidOpaqueIdentifiersArePreservedExactly(string id)
    {
        Assert.Equal(id, Event(id: id).Id);
        Assert.Equal(id, Snapshot(id: id).Id);
        Assert.Equal(id, new DeletionMarker(id, Origin).Id);
    }

    /// <summary>
    /// All three identifier boundaries accept 128 characters and reject 129 without silent truncation.
    /// </summary>
    [Fact]
    public void IdentifierLengthIsBoundedWithoutTruncation()
    {
        var accepted = new string('a', 128);
        var rejected = accepted + "b";
        Assert.Equal(accepted, Event(id: accepted).Id);
        Assert.Equal(accepted, Snapshot(id: accepted).Id);
        Assert.Equal(accepted, new DeletionMarker(accepted, Origin).Id);
        Assert.Throws<ArgumentException>(() => Event(id: rejected));
        Assert.Throws<ArgumentException>(() => Snapshot(id: rejected));
        Assert.Throws<ArgumentException>(() => new DeletionMarker(rejected, Origin));
    }

    /// <summary>
    /// Nonpositive revisions are rejected for snapshots and every source action, even when a delete's revision is optional.
    /// </summary>
    /// <param name="version">A revision outside the positive source-version range.</param>
    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void SuppliedVersionsMustBePositive(long version)
    {
        foreach (var type in new[] { CmsEventType.Publish, CmsEventType.Unpublish, CmsEventType.Delete })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CmsEvent(type, Id, Origin, version, Payload()));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => Snapshot(version: version));
    }

    /// <summary>
    /// Both content-bearing source actions require a revision and owned object; snapshot reconstruction also requires content.
    /// </summary>
    /// <param name="publish">Whether the source action is publish or unpublish.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ContentActionsRequireBothVersionAndPayload(bool publish)
    {
        var type = publish ? CmsEventType.Publish : CmsEventType.Unpublish;
        Assert.Throws<ArgumentException>(() => new CmsEvent(type, Id, Origin, payload: Payload()));
        Assert.Throws<ArgumentNullException>(() => new CmsEvent(type, Id, Origin, version: 1));
        Assert.Throws<ArgumentNullException>(() => new EntitySnapshot(Id, 1, null!, Origin, cmsPublished: true));
    }

    /// <summary>
    /// Versionless/content-free deletion is valid because source time alone orders a hard delete.
    /// </summary>
    [Fact]
    public void DeleteDoesNotRequireARevisionOrPayload()
    {
        var source = new CmsEvent(CmsEventType.Delete, Id, Origin);
        Assert.Null(source.Version);
        Assert.Null(source.Payload);
    }

    /// <summary>
    /// An unspecified, negative, or unknown source action is rejected before lifecycle evaluation.
    /// </summary>
    /// <param name="type">An unrecognized integer representation of the source action.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(99)]
    public void UnsupportedSourceActionsCannotEnterTheDomain(int type)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CmsEvent((CmsEventType)type, Id, Origin, 1, Payload()));
    }

    /// <summary>
    /// Events, snapshots, and markers normalize the same offset source instant to UTC without changing its meaning.
    /// </summary>
    [Fact]
    public void EverySourceWatermarkIsNormalizedToUtc()
    {
        var offsetTime = Origin.ToOffset(TimeSpan.FromHours(-7));
        var source = new CmsEvent(CmsEventType.Delete, Id, offsetTime);
        var snapshot = new EntitySnapshot(Id, 1, Payload(), offsetTime, cmsPublished: false);
        var marker = new DeletionMarker(Id, offsetTime);
        foreach (var timestamp in new[] { source.Timestamp, snapshot.CmsTimestamp, marker.Timestamp })
        {
            Assert.Equal(Origin, timestamp);
            Assert.Equal(TimeSpan.Zero, timestamp.Offset);
        }
    }

    /// <summary>
    /// Contradictory live/deleted state and a missing event are rejected without changing the supplied snapshot or marker.
    /// </summary>
    [Fact]
    public void LifecycleRejectsContradictoryOrMissingInputs()
    {
        var current = Snapshot();
        var marker = new DeletionMarker(Id, Origin);
        Assert.Throws<ArgumentNullException>(() => EntityLifecycle.Apply(null!));
        Assert.Throws<ArgumentException>(() => EntityLifecycle.Apply(Event(), current, marker));
        Assert.Equal(1, current.Version);
        Assert.Equal(Origin, marker.Timestamp);
    }

    /// <summary>
    /// Source state must match its exact ordinal identifier; differently cased or unrelated IDs cannot share a lifecycle.
    /// </summary>
    /// <param name="otherId">A differently cased or unrelated retained identifier.</param>
    [Theory]
    [InlineData("ARTICLE-001")]
    [InlineData("article-002")]
    public void LifecycleRejectsStateForAnotherOrdinalIdentifier(string otherId)
    {
        Assert.Throws<ArgumentException>(() => EntityLifecycle.Apply(Event(), Snapshot(id: otherId)));
        Assert.Throws<ArgumentException>(() => EntityLifecycle.Apply(Event(), deletion: new DeletionMarker(otherId, Origin)));
    }
}
