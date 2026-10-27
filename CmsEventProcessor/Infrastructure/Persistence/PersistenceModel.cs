using CmsEventProcessor.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;

namespace CmsEventProcessor.Infrastructure.Persistence;

/// <summary>
/// Defines the shared SQL schema used by write and read contexts without sharing their mutation behavior.
/// </summary>
internal static class PersistenceModel
{
    public const string BinaryCollation = "Latin1_General_100_BIN2";

    /// <summary>
    /// Maps current content, metadata-only deletions, and successful replay records with bounded keys and indexes.
    /// </summary>
    /// <param name="model">The EF model builder for either context.</param>
    internal static void Configure(ModelBuilder model)
    {
        var snapshot = model.Entity<SnapshotRow>();
        snapshot.ToTable("Snapshots", table =>
        {
            table.HasCheckConstraint("CK_Snapshots_PayloadObject", "ISJSON([Payload], OBJECT) = 1");
            table.HasCheckConstraint("CK_Snapshots_PositiveVersion", "[Version] > 0");
        });
        snapshot.HasKey(row => row.Id);
        snapshot.Property(row => row.Id).HasMaxLength(128).UseCollation(BinaryCollation);
        snapshot.Property(row => row.Payload).IsRequired().HasColumnType("nvarchar(max)");
        snapshot.Property(row => row.CmsTimestamp).HasPrecision(7);
        snapshot.Property(row => row.RowVersion).IsRowVersion();
        snapshot.HasIndex(row => row.Id).HasDatabaseName("IX_Snapshots_ReaderVisible")
            .HasFilter("[CmsPublished] = 1 AND [AdminDisabled] = 0")
            .IncludeProperties(row => new { row.Version, row.CmsTimestamp });

        var deletion = model.Entity<DeletionRow>();
        deletion.ToTable("DeletionMarkers");
        deletion.HasKey(row => row.Id);
        deletion.Property(row => row.Id).HasMaxLength(128).UseCollation(BinaryCollation);
        deletion.Property(row => row.Timestamp).HasPrecision(7);

        var replay = model.Entity<ReplayRow>();
        replay.ToTable("ReplayResults", table =>
        {
            table.HasCheckConstraint("CK_ReplayResults_ResponseObject", "ISJSON([ResponseJson], OBJECT) = 1");
            table.HasCheckConstraint("CK_ReplayResults_Expiry", "[ExpiresAt] > [CompletedAt]");
        });
        replay.HasKey(row => new { row.Principal, row.KeyHash });
        replay.Property(row => row.Principal).HasMaxLength(128).UseCollation(BinaryCollation);
        replay.Property(row => row.KeyHash).HasColumnType("binary(32)");
        replay.Property(row => row.BodyHash).HasColumnType("binary(32)").IsRequired();
        replay.Property(row => row.ResponseJson).IsRequired().HasColumnType("nvarchar(max)");
        replay.Property(row => row.CompletedAt).HasPrecision(7);
        replay.Property(row => row.ExpiresAt).HasPrecision(7);
        replay.HasIndex(row => row.ExpiresAt);
    }
}
