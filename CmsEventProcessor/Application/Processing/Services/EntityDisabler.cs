using CmsEventProcessor.Application.Processing.Contracts;
using CmsEventProcessor.Application.Processing.Models;
using CmsEventProcessor.Application.Processing.Options;
using CmsEventProcessor.Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CmsEventProcessor.Application.Processing.Services;

/// <summary>
/// Coordinates local disabling with ingestion through the same bounded, transaction-owned mutation resource.
/// </summary>
/// <param name="transactions">The shared mutation transaction factory.</param>
/// <param name="options">The single-attempt deadline and lock wait.</param>
/// <param name="logger">The logger receiving safe operation statuses without content or source identifiers.</param>
internal sealed class EntityDisabler(IMutationTransactionFactory transactions, IOptions<ProcessingOptions> options,
    ILogger<EntityDisabler> logger)
{
    /// <summary>
    /// Applies an idempotent local override without changing CMS fields or reviving a deleted entity.
    /// </summary>
    /// <param name="id">The validated exact source identifier.</param>
    /// <param name="cancellationToken">The originating caller cancellation token.</param>
    /// <returns>Success after commit, NotFound for absent/deleted state, or a safe operational failure.</returns>
    /// <exception cref="ArgumentException">The identifier violates domain invariants.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels the mutation.</exception>
    public async Task<MutationStatus> DisableAsync(string id, CancellationToken cancellationToken = default)
    {
        EntityRules.ValidateId(id);
        var correlation = Guid.NewGuid();
        await using var attempt = new MutationAttempt(options.Value, cancellationToken, logger);
        try
        {
            var transaction = await attempt.OpenAsync(transactions);
            if (!await transaction.AcquireAsync(options.Value.LockWait, attempt.Token))
            {
                logger.LogWarning("Local mutation {Correlation} could not acquire ownership.", correlation);
                return MutationStatus.StorageUnavailable;
            }
            var current = await transaction.ReadEntityAsync(id, attempt.Token);
            if (current.Snapshot is null)
            {
                return MutationStatus.NotFound;
            }
            var disabled = current.Snapshot.Disable();
            if (!ReferenceEquals(disabled, current.Snapshot))
            {
                await transaction.WriteEntityAsync(id, new StoredEntityState(disabled, null), attempt.Token);
            }
            await attempt.CommitAsync();
            logger.LogInformation("Local mutation {Correlation} committed.", correlation);
            return MutationStatus.Succeeded;
        }
        catch (Exception exception) when (MutationAttempt.IsOperationalFailure(exception))
        {
            return attempt.ClassifyFailure(exception, correlation);
        }
    }
}
