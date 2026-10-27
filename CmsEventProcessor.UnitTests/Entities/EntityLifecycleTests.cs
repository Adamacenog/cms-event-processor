using CmsEventProcessor.Domain.Entities.Models;
using CmsEventProcessor.Domain.Entities.Services;
using static CmsEventProcessor.UnitTests.Entities.LifecycleFixtures;

namespace CmsEventProcessor.UnitTests.Entities;

/// <summary>
/// Exercises timestamp/version precedence, publication ties, content conflicts, and hard-deletion lifecycle boundaries.
/// </summary>
public sealed class EntityLifecycleTests
{
    /// <summary>
    /// A first publish or unpublish accepts any positive observed revision, including gaps and the maximum 64-bit value.
    /// </summary>
    /// <param name="version">The first revision observed without requiring earlier delivery history.</param>
    /// <param name="published">Whether the incoming action publishes or retains unpublished content.</param>
    [Theory]
    [InlineData(1L, true)]
    [InlineData(7L, true)]
    [InlineData(long.MaxValue, true)]
    [InlineData(1L, false)]
    [InlineData(7L, false)]
    [InlineData(long.MaxValue, false)]
    public void FirstObservedRevisionCreatesTheRequestedPublicationState(long version, bool published)
    {
        var source = Event(published ? CmsEventType.Publish : CmsEventType.Unpublish, version);
        var result = EntityLifecycle.Apply(source);
        Assert.Equal(TransitionOutcome.Applied, result.Outcome);
        Assert.Equal(published ? TransitionReason.Published : TransitionReason.Unpublished, result.Reason);
        var snapshot = Assert.IsType<EntitySnapshot>(result.Snapshot);
        Assert.Equal(Id, snapshot.Id);
        Assert.Equal(version, snapshot.Version);
        Assert.Same(source.Payload, snapshot.Payload);
        Assert.Equal(Origin, snapshot.CmsTimestamp);
        Assert.Equal(published, snapshot.CmsPublished);
        Assert.False(snapshot.AdminDisabled);
        Assert.Null(result.Deletion);
    }

    /// <summary>
    /// Unpublishing an unseen revision stores its new content while preserving the independent administrator override.
    /// </summary>
    [Fact]
    public void UnseenUnpublishRetainsNewerContentWithoutMutatingItsPublishedPredecessor()
    {
        var current = Snapshot(disabled: true);
        var source = Event(CmsEventType.Unpublish, version: 2, seconds: 1, json: "{\"title\":\"Unseen revision\"}");
        var result = EntityLifecycle.Apply(source, current);
        Assert.Equal(TransitionOutcome.Applied, result.Outcome);
        Assert.Equal(TransitionReason.Unpublished, result.Reason);
        var snapshot = Assert.IsType<EntitySnapshot>(result.Snapshot);
        Assert.Equal(2, snapshot.Version);
        Assert.Same(source.Payload, snapshot.Payload);
        Assert.False(snapshot.CmsPublished);
        Assert.True(snapshot.AdminDisabled);
        Assert.False(snapshot.VisibleToReader);
        Assert.True(current.CmsPublished);
        Assert.Equal(1, current.Version);
        Assert.Equal("Example", current.Payload.Value.GetProperty("title").GetString());
    }

    /// <summary>
    /// A higher revision at an equal or later source time replaces content and publication while preserving local disabling.
    /// </summary>
    /// <param name="published">The source publication state requested by the higher revision.</param>
    /// <param name="seconds">An equal or newer source timestamp.</param>
    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 0)]
    [InlineData(true, 5)]
    [InlineData(false, 5)]
    public void NewerRevisionReplacesContentAndPreservesTheOverride(bool published, int seconds)
    {
        var current = Snapshot(version: 2, disabled: true, published: !published);
        var source = Event(published ? CmsEventType.Publish : CmsEventType.Unpublish,
            version: 9, seconds: seconds, json: "{\"new\":true}");
        var result = EntityLifecycle.Apply(source, current);
        Assert.Equal(TransitionOutcome.Applied, result.Outcome);
        var snapshot = Assert.IsType<EntitySnapshot>(result.Snapshot);
        Assert.Equal(9, snapshot.Version);
        Assert.Same(source.Payload, snapshot.Payload);
        Assert.Equal(Origin.AddSeconds(seconds), snapshot.CmsTimestamp);
        Assert.Equal(published, snapshot.CmsPublished);
        Assert.True(snapshot.AdminDisabled);
    }

    /// <summary>
    /// Source time takes precedence over a claimed higher revision, so an older publish/unpublish cannot regress state.
    /// </summary>
    /// <param name="published">Whether the stale higher-version action requests publication.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HigherRevisionWithOlderTimestampIsStale(bool published)
    {
        var current = Snapshot(version: 3, seconds: 10, disabled: true);
        var result = EntityLifecycle.Apply(Event(published ? CmsEventType.Publish : CmsEventType.Unpublish,
            version: 99, seconds: 9, json: "{\"changed\":true}"), current);
        Assert.Equal(TransitionOutcome.Skipped, result.Outcome);
        Assert.Equal(TransitionReason.Stale, result.Reason);
        Assert.Same(current, result.Snapshot);
        Assert.Null(result.Deletion);
    }

    /// <summary>
    /// A lower revision remains stale even with different content and a later timestamp; its skip does not advance the watermark.
    /// </summary>
    /// <param name="seconds">The equal or later timestamp claimed by the lower revision.</param>
    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    public void SupersededRevisionIsStaleWithoutPayloadConflictOrWatermarkAdvance(int seconds)
    {
        var current = Snapshot(version: 3, seconds: 10);
        var result = EntityLifecycle.Apply(Event(CmsEventType.Unpublish, version: 2,
            seconds: seconds, json: "{\"old\":true}"), current);
        Assert.Equal(TransitionOutcome.Skipped, result.Outcome);
        Assert.Equal(TransitionReason.Stale, result.Reason);
        Assert.Same(current, result.Snapshot);
        Assert.Equal(Origin.AddSeconds(10), current.CmsTimestamp);
    }

    /// <summary>
    /// Repeating the same revision, parsed content, source time, and publication is a duplicate with no state replacement.
    /// </summary>
    /// <param name="published">The matching retained and incoming publication state.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IdenticalDeliveryIsADuplicate(bool published)
    {
        var current = Snapshot(published: published, disabled: true);
        var result = EntityLifecycle.Apply(Event(published ? CmsEventType.Publish : CmsEventType.Unpublish,
            json: "{ \"count\" : 1.0, \"title\" : \"Example\" }"), current);
        Assert.Equal(TransitionOutcome.Skipped, result.Outcome);
        Assert.Equal(TransitionReason.Duplicate, result.Reason);
        Assert.Same(current, result.Snapshot);
    }

    /// <summary>
    /// A later same-version event applies even without a publication change, advances source time, and blocks an intervening action.
    /// </summary>
    /// <param name="wasPublished">The original CMS publication state.</param>
    /// <param name="publish">The later same-version action, including republish and unchanged publication cases.</param>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void LaterSameVersionAdvancesTheWatermarkAndRejectsAnInterveningEvent(bool wasPublished, bool publish)
    {
        var current = Snapshot(seconds: 10, published: wasPublished, disabled: true);
        var result = EntityLifecycle.Apply(Event(publish ? CmsEventType.Publish : CmsEventType.Unpublish, seconds: 20), current);
        Assert.Equal(TransitionOutcome.Applied, result.Outcome);
        Assert.Equal(publish ? TransitionReason.Published : TransitionReason.Unpublished, result.Reason);
        var snapshot = Assert.IsType<EntitySnapshot>(result.Snapshot);
        Assert.Equal(Origin.AddSeconds(20), snapshot.CmsTimestamp);
        Assert.Equal(publish, snapshot.CmsPublished);
        Assert.True(snapshot.AdminDisabled);
        Assert.Equal(current.Version, snapshot.Version);
        Assert.True(current.Payload.HasSameContent(snapshot.Payload));
        var intervening = EntityLifecycle.Apply(Event(publish ? CmsEventType.Unpublish : CmsEventType.Publish, seconds: 15), snapshot);
        Assert.Equal(TransitionOutcome.Skipped, intervening.Outcome);
        Assert.Equal(TransitionReason.Stale, intervening.Reason);
        Assert.Same(snapshot, intervening.Snapshot);
        var interveningDelete = EntityLifecycle.Apply(Event(CmsEventType.Delete, seconds: 15), snapshot);
        Assert.Equal(TransitionReason.Stale, interveningDelete.Reason);
        Assert.Same(snapshot, interveningDelete.Snapshot);
    }

    /// <summary>
    /// At equal time/version/content, unpublish wins regardless of delivery order and a losing publish is stale.
    /// </summary>
    /// <param name="wasPublished">Whether the retained snapshot is the publish or unpublish side of the tie.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnpublishWinsEqualTimeAndVersionTies(bool wasPublished)
    {
        var current = Snapshot(published: wasPublished, disabled: true);
        var result = EntityLifecycle.Apply(Event(wasPublished ? CmsEventType.Unpublish : CmsEventType.Publish), current);
        Assert.Equal(wasPublished ? TransitionOutcome.Applied : TransitionOutcome.Skipped, result.Outcome);
        Assert.Equal(wasPublished ? TransitionReason.Unpublished : TransitionReason.Stale, result.Reason);
        var snapshot = Assert.IsType<EntitySnapshot>(result.Snapshot);
        Assert.False(snapshot.CmsPublished);
        Assert.True(snapshot.AdminDisabled);
        Assert.Equal(Origin, snapshot.CmsTimestamp);
    }

    /// <summary>
    /// Different payloads at the current revision conflict before equal-time publication tie-breaking and never mutate state.
    /// </summary>
    /// <param name="seconds">The equal or later source time of the conflicting content.</param>
    /// <param name="publish">Whether the conflicting source action publishes or unpublishes.</param>
    [Theory]
    [InlineData(0, true)]
    [InlineData(0, false)]
    [InlineData(5, true)]
    [InlineData(5, false)]
    public void SameVersionDifferentContentConflictsWithoutChangingState(int seconds, bool publish)
    {
        var current = Snapshot(published: !publish, disabled: true);
        var result = EntityLifecycle.Apply(Event(publish ? CmsEventType.Publish : CmsEventType.Unpublish,
            seconds: seconds, json: "{\"title\":\"Conflicting content\"}"), current);
        Assert.Equal(TransitionOutcome.Conflict, result.Outcome);
        Assert.Equal(TransitionReason.VersionPayloadConflict, result.Reason);
        Assert.Same(current, result.Snapshot);
        Assert.Null(result.Deletion);
        Assert.Equal("Example", current.Payload.Value.GetProperty("title").GetString());
        Assert.Equal(Origin, current.CmsTimestamp);
    }

    /// <summary>
    /// An older same-version payload is stale before content comparison, rather than causing a historical conflict.
    /// </summary>
    [Fact]
    public void OlderSameVersionDifferentContentIsStaleBeforeConflictDetection()
    {
        var current = Snapshot(seconds: 10);
        var result = EntityLifecycle.Apply(Event(seconds: 9, json: "{\"different\":true}"), current);
        Assert.Equal(TransitionOutcome.Skipped, result.Outcome);
        Assert.Equal(TransitionReason.Stale, result.Reason);
        Assert.Same(current, result.Snapshot);
    }

    /// <summary>
    /// Delete compares source time alone: older deletion is stale, while equal/newer deletion drops content and the override.
    /// </summary>
    /// <param name="seconds">The deletion time relative to a snapshot at the origin.</param>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void DeleteUsesSourceTimeAndEndsTheRetainedLifecycle(int seconds)
    {
        var current = Snapshot(version: 42, disabled: true);
        var result = EntityLifecycle.Apply(Event(CmsEventType.Delete, seconds: seconds), current);
        if (seconds < 0)
        {
            Assert.Equal(TransitionOutcome.Skipped, result.Outcome);
            Assert.Equal(TransitionReason.Stale, result.Reason);
            Assert.Same(current, result.Snapshot);
            Assert.Null(result.Deletion);
            return;
        }
        Assert.Equal(TransitionOutcome.Applied, result.Outcome);
        Assert.Equal(TransitionReason.Deleted, result.Reason);
        Assert.Null(result.Snapshot);
        var marker = Assert.IsType<DeletionMarker>(result.Deletion);
        Assert.Equal(Id, marker.Id);
        Assert.Equal(Origin.AddSeconds(seconds), marker.Timestamp);
        Assert.Equal(new[] { "Id", "Timestamp" }, typeof(DeletionMarker).GetProperties()
            .Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Optional delete revisions and payloads do not participate in ordering or survive in replacement deletion metadata.
    /// </summary>
    /// <param name="version">The absent, lower, or maximum optional delete revision.</param>
    [Theory]
    [InlineData(null)]
    [InlineData(1L)]
    [InlineData(long.MaxValue)]
    public void DeleteIgnoresOptionalContentAndVersion(long? version)
    {
        var current = Snapshot(version: 42, disabled: true);
        var source = new CmsEvent(CmsEventType.Delete, Id, Origin, version, Payload("{\"discard\":true}"));
        var result = EntityLifecycle.Apply(source, current);
        Assert.Equal(TransitionOutcome.Applied, result.Outcome);
        Assert.Equal(TransitionReason.Deleted, result.Reason);
        Assert.Null(result.Snapshot);
        Assert.NotNull(result.Deletion);
    }

    /// <summary>
    /// Deleting an unknown identifier creates a metadata-only barrier against earlier first delivery.
    /// </summary>
    [Fact]
    public void UnknownDeleteCreatesADeletionMarker()
    {
        var result = EntityLifecycle.Apply(Event(CmsEventType.Delete));
        Assert.Equal(TransitionOutcome.Applied, result.Outcome);
        Assert.Equal(TransitionReason.Deleted, result.Reason);
        Assert.Null(result.Snapshot);
        Assert.Equal(Origin, Assert.IsType<DeletionMarker>(result.Deletion).Timestamp);
    }

    /// <summary>
    /// A deletion marker skips older deletes, recognizes equal deletes as duplicates, and advances only on a newer delete.
    /// </summary>
    /// <param name="seconds">The incoming delete time relative to the existing marker.</param>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void RepeatedDeletesNeverMoveTheMarkerBackwards(int seconds)
    {
        var marker = new DeletionMarker(Id, Origin);
        var result = EntityLifecycle.Apply(Event(CmsEventType.Delete, seconds: seconds), deletion: marker);
        Assert.Null(result.Snapshot);
        Assert.Equal(seconds > 0 ? TransitionOutcome.Applied : TransitionOutcome.Skipped, result.Outcome);
        Assert.Equal(seconds > 0 ? TransitionReason.Deleted : seconds == 0 ? TransitionReason.Duplicate : TransitionReason.Stale, result.Reason);
        if (seconds <= 0)
        {
            Assert.Same(marker, result.Deletion);
        }
        else
        {
            Assert.Equal(Origin.AddSeconds(seconds), Assert.IsType<DeletionMarker>(result.Deletion).Timestamp);
            Assert.Equal(Origin, marker.Timestamp);
        }
    }

    /// <summary>
    /// Publish/unpublish at or before deletion cannot recreate content, even when claiming a much higher revision.
    /// </summary>
    /// <param name="seconds">The older or equal source time relative to the deletion marker.</param>
    /// <param name="publish">The requested source publication action.</param>
    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    public void MarkerBlocksStaleOrEqualTimeResurrection(int seconds, bool publish)
    {
        var marker = new DeletionMarker(Id, Origin);
        var result = EntityLifecycle.Apply(Event(publish ? CmsEventType.Publish : CmsEventType.Unpublish,
            version: 99, seconds: seconds), deletion: marker);
        Assert.Equal(TransitionOutcome.Skipped, result.Outcome);
        Assert.Equal(TransitionReason.Stale, result.Reason);
        Assert.Null(result.Snapshot);
        Assert.Same(marker, result.Deletion);
    }

    /// <summary>
    /// Strictly later publish/unpublish begins a fresh revision sequence and never inherits the hard-deleted local override.
    /// </summary>
    /// <param name="publish">Whether the recreated lifecycle starts published or unpublished.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LaterContentRecreatesAFreshLifecycleWithoutAnInheritedOverride(bool publish)
    {
        var deleted = EntityLifecycle.Apply(Event(CmsEventType.Delete), Snapshot(version: 42, disabled: true));
        var source = Event(publish ? CmsEventType.Publish : CmsEventType.Unpublish, version: 1, seconds: 1,
            json: "{\"fresh\":true}");
        var result = EntityLifecycle.Apply(source, deletion: deleted.Deletion);
        Assert.Equal(TransitionOutcome.Applied, result.Outcome);
        var snapshot = Assert.IsType<EntitySnapshot>(result.Snapshot);
        Assert.Equal(1, snapshot.Version);
        Assert.Equal(publish, snapshot.CmsPublished);
        Assert.Equal(publish, snapshot.VisibleToReader);
        Assert.False(snapshot.AdminDisabled);
        Assert.Same(source.Payload, snapshot.Payload);
        Assert.Null(result.Deletion);
    }

    /// <summary>
    /// Equal instants with different offsets are treated as a timestamp tie and a versionless delete still wins.
    /// </summary>
    [Fact]
    public void EquivalentOffsetsUseTheSameUtcWatermark()
    {
        var current = Snapshot();
        var offsetTime = Origin.ToOffset(TimeSpan.FromHours(5.5));
        var duplicate = EntityLifecycle.Apply(new CmsEvent(CmsEventType.Publish, Id, offsetTime, 1, Payload()), current);
        Assert.Equal(TransitionReason.Duplicate, duplicate.Reason);
        var deleted = EntityLifecycle.Apply(new CmsEvent(CmsEventType.Delete, Id, offsetTime), current);
        Assert.Equal(TransitionReason.Deleted, deleted.Reason);
        Assert.Null(deleted.Snapshot);
        Assert.Equal(Origin, Assert.IsType<DeletionMarker>(deleted.Deletion).Timestamp);
    }
}
