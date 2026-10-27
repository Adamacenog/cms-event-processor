namespace CmsEventProcessor.IntegrationTests.Sql;

/// <summary>
/// Advances retention time deterministically without sleeping or changing source-event ordering.
/// </summary>
internal sealed class TestTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Returns the test-controlled UTC retention instant.
    /// </summary>
    /// <returns>The current synthetic processing time.</returns>
    public override DateTimeOffset GetUtcNow() => Now;
}
