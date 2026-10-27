using System.Security.Cryptography;
using CmsEventProcessor.Application.Processing.Contracts;
using CmsEventProcessor.Application.Processing.Models;
using CmsEventProcessor.Application.Processing.Options;
using CmsEventProcessor.Domain.Entities.Models;
using CmsEventProcessor.Domain.Entities.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CmsEventProcessor.Application.Processing.Services;

/// <summary>
/// Applies validated events in order and returns success only after entity state and scoped replay metadata commit together.
/// </summary>
/// <param name="transactions">The request-owned transaction factory, shared with administrator mutations.</param>
/// <param name="options">The validated single-attempt deadline and lock wait.</param>
/// <param name="clock">The UTC retention clock; source event ordering does not use this clock.</param>
/// <param name="logger">The logger receiving correlation, event position, and safe decisions without content or identifiers.</param>
internal sealed class BatchProcessor(IMutationTransactionFactory transactions, IOptions<ProcessingOptions> options,
    TimeProvider clock, ILogger<BatchProcessor> logger)
{
    /// <summary>
    /// Executes one atomic attempt, replaying an unexpired success before domain evaluation and never retrying internally.
    /// </summary>
    /// <param name="request">The immutable validated events, authenticated principal, and exact request/key digests.</param>
    /// <param name="cancellationToken">The caller cancellation token linked to the one processing deadline.</param>
    /// <returns>Committed metadata or a safe failure classification; conflicts contain no tentative successful outcomes.</returns>
    /// <exception cref="ArgumentNullException">The validated request is null.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels; a cancellation around commit can still be ambiguous.</exception>
    public async Task<ProcessingResult> ProcessAsync(BatchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var correlation = Guid.NewGuid();
        int? activeEvent = null;
        await using var attempt = new MutationAttempt(options.Value, cancellationToken, logger);
        try
        {
            var transaction = await attempt.OpenAsync(transactions);
            if (!await transaction.AcquireAsync(options.Value.LockWait, attempt.Token))
            {
                logger.LogWarning("Batch {BatchId} could not acquire mutation ownership.", correlation);
                return new ProcessingResult(MutationStatus.StorageUnavailable);
            }
            var now = clock.GetUtcNow();
            if (!request.KeyHash.IsEmpty)
            {
                var replay = await transaction.ReadReplayAsync(request.Principal, request.KeyHash, attempt.Token);
                if (replay is not null && replay.ExpiresAt > now)
                {
                    if (!CryptographicOperations.FixedTimeEquals(replay.BodyHash.Span, request.BodyHash.Span))
                    {
                        logger.LogWarning("Batch {BatchId} rejected: idempotency key conflict.", correlation);
                        return new ProcessingResult(MutationStatus.IdempotencyConflict);
                    }
                    logger.LogInformation("Replaying committed batch {BatchId}.", replay.Response.BatchId);
                    return new ProcessingResult(MutationStatus.Succeeded, replay.Response, isReplay: true);
                }
            }

            await transaction.DeleteExpiredAsync(now, 100, attempt.Token);
            var outcomes = new List<EventOutcome>();
            for (var index = 0; index < request.Events.Count; index++)
            {
                attempt.Token.ThrowIfCancellationRequested();
                var source = request.Events[index];
                activeEvent = index;
                var current = await transaction.ReadEntityAsync(source.Id, attempt.Token);
                var transition = EntityLifecycle.Apply(source, current.Snapshot, current.Deletion);
                if (transition.Outcome == TransitionOutcome.Conflict)
                {
                    logger.LogWarning("Batch {BatchId} rejected at event {Index}: version payload conflict; no batch success.",
                        correlation, index);
                    return new ProcessingResult(MutationStatus.VersionConflict, eventIndex: index);
                }
                if (transition.Outcome == TransitionOutcome.Applied)
                {
                    await transaction.WriteEntityAsync(source.Id, new StoredEntityState(transition.Snapshot, transition.Deletion), attempt.Token);
                }
                outcomes.Add(new EventOutcome(index, source.Id, transition.Outcome.ToString().ToLowerInvariant(),
                    transition.Reason.ToString().ToLowerInvariant()));
            }

            var success = new BatchSuccess(correlation, outcomes);
            if (!request.KeyHash.IsEmpty)
            {
                var completed = clock.GetUtcNow();
                await transaction.WriteReplayAsync(new ReplayRecord(request.Principal, request.KeyHash,
                    request.BodyHash, success, completed, completed.AddDays(7)), attempt.Token);
            }
            await attempt.CommitAsync();
            foreach (var outcome in outcomes)
            {
                logger.LogInformation("Committed batch {BatchId} event {Index}: {Outcome}/{Reason}.",
                    success.BatchId, outcome.Index, outcome.Outcome, outcome.ReasonCode);
            }
            return new ProcessingResult(MutationStatus.Succeeded, success);
        }
        catch (Exception exception) when (MutationAttempt.IsOperationalFailure(exception))
        {
            logger.LogWarning("Batch {BatchId} interrupted; last evaluated event {Index}. No committed outcomes reported.",
                correlation, activeEvent);
            return new ProcessingResult(attempt.ClassifyFailure(exception, correlation));
        }
        catch (Exception exception)
        {
            logger.LogError("Batch {BatchId} aborted; last evaluated event {Index}; failure type {FailureType}.",
                correlation, activeEvent, exception.GetType().Name);
            throw;
        }
    }
}
