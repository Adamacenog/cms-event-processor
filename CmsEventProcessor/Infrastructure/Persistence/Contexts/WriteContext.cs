using CmsEventProcessor.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;

namespace CmsEventProcessor.Infrastructure.Persistence.Contexts;

/// <summary>
/// Owns tracked SQL writes and migrations; each processing attempt receives its own instance and transaction.
/// </summary>
/// <param name="options">The configured SQL connection, non-retrying strategy, and diagnostic options.</param>
internal sealed class WriteContext(DbContextOptions<WriteContext> options) : DbContext(options)
{
    public DbSet<SnapshotRow> Snapshots => Set<SnapshotRow>();
    public DbSet<DeletionRow> Deletions => Set<DeletionRow>();
    public DbSet<ReplayRow> Replays => Set<ReplayRow>();

    /// <summary>
    /// Builds the explicitly versioned state/replay schema.
    /// </summary>
    /// <param name="modelBuilder">The EF schema builder.</param>
    protected override void OnModelCreating(ModelBuilder modelBuilder) => PersistenceModel.Configure(modelBuilder);
}
