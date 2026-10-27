using System.ComponentModel.DataAnnotations;

namespace CmsEventProcessor.IntegrationTests;

/// <summary>
/// Defines test-only input constraints for model-binding and validation privacy checks.
/// </summary>
public sealed record ProbeInput
{
    /// <summary>
    /// Receives confidential strings in negative tests to verify conversion-error redaction.
    /// </summary>
    public int Count { get; init; }
    /// <summary>
    /// Forces the missing-required-field validation path without depending on future business DTOs.
    /// </summary>
    [Required]
    public string? Value { get; init; }
}
