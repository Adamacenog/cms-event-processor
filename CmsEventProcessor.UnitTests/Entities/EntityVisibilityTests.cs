using static CmsEventProcessor.UnitTests.Entities.LifecycleFixtures;

namespace CmsEventProcessor.UnitTests.Entities;

/// <summary>
/// Verifies that CMS publication and the local administrator override remain independent visibility decisions.
/// </summary>
public sealed class EntityVisibilityTests
{
    /// <summary>
    /// Readers see only published snapshots without a local disable; retained unpublished/disabled states remain representable.
    /// </summary>
    /// <param name="published">The source publication flag.</param>
    /// <param name="disabled">The independent local administrator flag.</param>
    /// <param name="visible">Whether the reader visibility expression should admit the snapshot.</param>
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void ReaderVisibilityRequiresPublicationAndNoLocalOverride(bool published, bool disabled, bool visible)
    {
        Assert.Equal(visible, Snapshot(published: published, disabled: disabled).VisibleToReader);
    }

    /// <summary>
    /// Local disable creates a hidden snapshot while preserving every CMS field and leaving the original snapshot unchanged.
    /// </summary>
    /// <param name="published">Whether the source currently publishes the retained content.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DisableChangesOnlyTheLocalOverride(bool published)
    {
        var current = Snapshot(version: 17, seconds: 10, published: published);
        var disabled = current.Disable();
        Assert.NotSame(current, disabled);
        Assert.True(disabled.AdminDisabled);
        Assert.False(disabled.VisibleToReader);
        Assert.Equal(current.Id, disabled.Id);
        Assert.Equal(current.Version, disabled.Version);
        Assert.Same(current.Payload, disabled.Payload);
        Assert.Equal(current.CmsTimestamp, disabled.CmsTimestamp);
        Assert.Equal(current.CmsPublished, disabled.CmsPublished);
        Assert.False(current.AdminDisabled);
        Assert.Same(disabled, disabled.Disable());
    }
}
