namespace CmsEventProcessor.Domain.Entities;

/// <summary>
/// Enforces the identifier and revision invariants shared by incoming events and retained lifecycle state.
/// </summary>
internal static class EntityRules
{
    /// <summary>
    /// Rejects unusable identifiers without trimming, normalizing, or echoing their values.
    /// </summary>
    /// <param name="id">The opaque, case-sensitive identifier to validate without modifying it.</param>
    /// <exception cref="ArgumentNullException">The identifier is null.</exception>
    /// <exception cref="ArgumentException">The identifier exceeds 128 characters, is empty, or contains boundary whitespace or control characters.</exception>
    internal static void ValidateId(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (id.Length is < 1 or > 128 || char.IsWhiteSpace(id[0]) || char.IsWhiteSpace(id[^1]) ||
            id.Any(char.IsControl))
        {
            throw new ArgumentException("Entity identifiers must contain 1-128 characters without boundary whitespace or control characters.", nameof(id));
        }
    }

    /// <summary>
    /// Allows missing historical revisions while rejecting nonpositive source versions.
    /// </summary>
    /// <param name="version">The source revision within the current entity lifecycle.</param>
    /// <exception cref="ArgumentOutOfRangeException">The version is not positive.</exception>
    internal static void ValidateVersion(long version)
    {
        if (version < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "Entity versions must be positive.");
        }
    }
}
