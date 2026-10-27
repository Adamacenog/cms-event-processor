namespace CmsEventProcessor.Domain.Entities.Models;

/// <summary>
/// Keeps current content, source ordering, and the independent local visibility override in an immutable snapshot.
/// </summary>
internal sealed class EntitySnapshot
{
    /// <summary>
    /// Reconstructs valid current state without conflating source revision, publication, or local disabling.
    /// </summary>
    /// <param name="id">The opaque identifier preserved exactly from the CMS.</param>
    /// <param name="version">The positive current content revision; historical gaps are allowed.</param>
    /// <param name="payload">The owned current JSON object, including unpublished confidential data.</param>
    /// <param name="cmsTimestamp">The last accepted source event time, normalized to UTC.</param>
    /// <param name="cmsPublished">Whether the CMS currently publishes this snapshot.</param>
    /// <param name="adminDisabled">Whether a local administrator independently hides this lifecycle.</param>
    /// <exception cref="ArgumentNullException">The identifier or payload is null.</exception>
    /// <exception cref="ArgumentException">The identifier violates its bounded, opaque format.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The version is not positive.</exception>
    public EntitySnapshot(string id, long version, EntityPayload payload, DateTimeOffset cmsTimestamp,
        bool cmsPublished, bool adminDisabled = false)
    {
        EntityRules.ValidateId(id);
        EntityRules.ValidateVersion(version);
        ArgumentNullException.ThrowIfNull(payload);
        Id = id;
        Version = version;
        Payload = payload;
        CmsTimestamp = cmsTimestamp.ToUniversalTime();
        CmsPublished = cmsPublished;
        AdminDisabled = adminDisabled;
    }

    public string Id { get; }
    public long Version { get; }
    public EntityPayload Payload { get; }
    public DateTimeOffset CmsTimestamp { get; }
    public bool CmsPublished { get; }
    public bool AdminDisabled { get; }

    /// <summary>
    /// Requires both source publication and the absence of an independent local disable.
    /// </summary>
    public bool VisibleToReader => CmsPublished && !AdminDisabled;

    /// <summary>
    /// Applies the local override without changing content, source time, version, or publication state.
    /// </summary>
    /// <returns>The same snapshot when already disabled, otherwise a new snapshot preserving every CMS field.</returns>
    public EntitySnapshot Disable() => AdminDisabled ? this :
        new EntitySnapshot(Id, Version, Payload, CmsTimestamp, CmsPublished, adminDisabled: true);
}
