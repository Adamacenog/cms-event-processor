using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CmsEventProcessor.Infrastructure.Persistence.Migrations;

/// <summary>
/// Creates the initial content, deletion-marker, and successful replay schema.
/// </summary>
public partial class InitialProcessingSchema : Migration
{
    /// <summary>
    /// Creates bounded binary keys, JSON checks, concurrency tokens, and read/expiry indexes.
    /// </summary>
    /// <param name="migrationBuilder">The migration operations builder.</param>
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "DeletionMarkers",
            columns: table => new
            {
                Id = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                Timestamp = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DeletionMarkers", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "ReplayResults",
            columns: table => new
            {
                Principal = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                KeyHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                BodyHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                ResponseJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ReplayResults", x => new { x.Principal, x.KeyHash });
                table.CheckConstraint("CK_ReplayResults_Expiry", "[ExpiresAt] > [CompletedAt]");
                table.CheckConstraint("CK_ReplayResults_ResponseObject", "ISJSON([ResponseJson], OBJECT) = 1");
            });

        migrationBuilder.CreateTable(
            name: "Snapshots",
            columns: table => new
            {
                Id = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false),
                CmsTimestamp = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                CmsPublished = table.Column<bool>(type: "bit", nullable: false),
                AdminDisabled = table.Column<bool>(type: "bit", nullable: false),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Snapshots", x => x.Id);
                table.CheckConstraint("CK_Snapshots_PayloadObject", "ISJSON([Payload], OBJECT) = 1");
                table.CheckConstraint("CK_Snapshots_PositiveVersion", "[Version] > 0");
            });

        migrationBuilder.CreateIndex(
            name: "IX_ReplayResults_ExpiresAt",
            table: "ReplayResults",
            column: "ExpiresAt");

        migrationBuilder.CreateIndex(
            name: "IX_Snapshots_ReaderVisible",
            table: "Snapshots",
            column: "Id",
            filter: "[CmsPublished] = 1 AND [AdminDisabled] = 0")
            .Annotation("SqlServer:Include", new[] { "Version", "CmsTimestamp" });
    }

    /// <summary>
    /// Removes this schema when an operator explicitly rolls the migration back.
    /// </summary>
    /// <param name="migrationBuilder">The migration operations builder.</param>
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "DeletionMarkers");

        migrationBuilder.DropTable(
            name: "ReplayResults");

        migrationBuilder.DropTable(
            name: "Snapshots");
    }
}
