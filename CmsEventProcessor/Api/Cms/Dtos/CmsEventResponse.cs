namespace CmsEventProcessor.Api.Cms.Dtos;

/// <summary>
/// Reports one committed event decision without returning confidential content.
/// </summary>
/// <param name="Index">The zero-based input position.</param>
/// <param name="Id">The original case-sensitive source identifier.</param>
/// <param name="Outcome">Applied or skipped.</param>
/// <param name="ReasonCode">The safe lifecycle reason.</param>
internal sealed record CmsEventResponse(int Index, string Id, string Outcome, string ReasonCode);
