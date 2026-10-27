using CmsEventProcessor.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;

namespace CmsEventProcessor.Infrastructure.Persistence.Contexts;

/// <summary>
/// Defaults reads to no tracking and rejects all SaveChanges overloads as an application-level read boundary.
/// </summary>
internal sealed class ReadContext : DbContext
{
    /// <summary>
    /// Configures no-tracking reads without granting mutation permission through this context.
    /// </summary>
    /// <param name="options">The independently configured read connection.</param>
    public ReadContext(DbContextOptions<ReadContext> options) : base(options)
    {
        ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        ChangeTracker.AutoDetectChangesEnabled = false;
    }

    public DbSet<SnapshotRow> Snapshots => Set<SnapshotRow>();

    /// <summary>
    /// Uses the same schema mappings as the writer without inheriting its save behavior.
    /// </summary>
    /// <param name="modelBuilder">The EF schema builder.</param>
    protected override void OnModelCreating(ModelBuilder modelBuilder) => PersistenceModel.Configure(modelBuilder);

    /// <summary>
    /// Rejects synchronous saves, including calls with no pending tracked changes.
    /// </summary>
    /// <returns>This method never returns a saved-row count.</returns>
    /// <exception cref="InvalidOperationException">Always thrown because this context is read-only.</exception>
    public override int SaveChanges() => throw ReadOnlyFailure();

    /// <summary>
    /// Rejects synchronous saves regardless of change-acceptance settings.
    /// </summary>
    /// <param name="acceptAllChangesOnSuccess">Ignored because writes are prohibited.</param>
    /// <returns>This method never returns a saved-row count.</returns>
    /// <exception cref="InvalidOperationException">Always thrown because this context is read-only.</exception>
    public override int SaveChanges(bool acceptAllChangesOnSuccess) => throw ReadOnlyFailure();

    /// <summary>
    /// Rejects asynchronous saves before sending any database command.
    /// </summary>
    /// <param name="cancellationToken">Ignored because writes are prohibited.</param>
    /// <returns>This method never returns a save task.</returns>
    /// <exception cref="InvalidOperationException">Always thrown because this context is read-only.</exception>
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => throw ReadOnlyFailure();

    /// <summary>
    /// Rejects asynchronous saves regardless of cancellation or change-acceptance settings.
    /// </summary>
    /// <param name="acceptAllChangesOnSuccess">Ignored because writes are prohibited.</param>
    /// <param name="cancellationToken">Ignored because writes are prohibited.</param>
    /// <returns>This method never returns a save task.</returns>
    /// <exception cref="InvalidOperationException">Always thrown because this context is read-only.</exception>
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default) =>
        throw ReadOnlyFailure();

    /// <summary>
    /// Creates a safe write-rejection exception without connection details.
    /// </summary>
    /// <returns>The invariant failure used by every SaveChanges overload.</returns>
    private static InvalidOperationException ReadOnlyFailure() => new("The read context does not permit SaveChanges.");
}
