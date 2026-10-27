namespace CmsEventProcessor.Api.Health.Dtos;

/// <summary>
/// Exposes only a minimal process/readiness status, never dependency details or exception content.
/// </summary>
/// <param name="Status">The fixed live, ready, or unavailable value.</param>
internal sealed record HealthResponse(string Status);
