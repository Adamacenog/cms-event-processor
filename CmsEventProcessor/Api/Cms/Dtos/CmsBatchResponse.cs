namespace CmsEventProcessor.Api.Cms.Dtos;

/// <summary>
/// Returns the committed batch correlation and final outcomes, including unchanged metadata on keyed replay.
/// </summary>
/// <param name="BatchId">The stable identifier of the acknowledged transaction.</param>
/// <param name="Outcomes">Final applied/skipped outcomes in submitted order.</param>
internal sealed record CmsBatchResponse(Guid BatchId, IReadOnlyList<CmsEventResponse> Outcomes);
