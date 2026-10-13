using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OtantikPos.Node.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeletionApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StockMovements_SourceId_Reason_RawMaterialId",
                schema: "inventory",
                table: "StockMovements");

            migrationBuilder.AddColumn<Guid>(
                name: "ReversalOf",
                schema: "inventory",
                table: "StockMovements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "VoidedAtUtc",
                schema: "inventory",
                table: "StockCounts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VoidedBy",
                schema: "inventory",
                table: "StockCounts",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ArchivedAtUtc",
                schema: "inventory",
                table: "RawMaterials",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArchivedBy",
                schema: "inventory",
                table: "RawMaterials",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "VoidedAtUtc",
                schema: "inventory",
                table: "PurchaseInvoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VoidedBy",
                schema: "inventory",
                table: "PurchaseInvoices",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DeletionRequests",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Module = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    Summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RequestedById = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RequestedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RequestedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DecidedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DecidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecisionNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeletionRequests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_SourceId_Reason_RawMaterialId_ReversalOf",
                schema: "inventory",
                table: "StockMovements",
                columns: new[] { "SourceId", "Reason", "RawMaterialId", "ReversalOf" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_DeletionRequests_Module_RecordId",
                schema: "inventory",
                table: "DeletionRequests",
                columns: new[] { "Module", "RecordId" },
                unique: true,
                filter: "\"Status\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_DeletionRequests_Status_RequestedAtUtc",
                schema: "inventory",
                table: "DeletionRequests",
                columns: new[] { "Status", "RequestedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeletionRequests",
                schema: "inventory");

            migrationBuilder.DropIndex(
                name: "IX_StockMovements_SourceId_Reason_RawMaterialId_ReversalOf",
                schema: "inventory",
                table: "StockMovements");

            migrationBuilder.DropColumn(
                name: "ReversalOf",
                schema: "inventory",
                table: "StockMovements");

            migrationBuilder.DropColumn(
                name: "VoidedAtUtc",
                schema: "inventory",
                table: "StockCounts");

            migrationBuilder.DropColumn(
                name: "VoidedBy",
                schema: "inventory",
                table: "StockCounts");

            migrationBuilder.DropColumn(
                name: "ArchivedAtUtc",
                schema: "inventory",
                table: "RawMaterials");

            migrationBuilder.DropColumn(
                name: "ArchivedBy",
                schema: "inventory",
                table: "RawMaterials");

            migrationBuilder.DropColumn(
                name: "VoidedAtUtc",
                schema: "inventory",
                table: "PurchaseInvoices");

            migrationBuilder.DropColumn(
                name: "VoidedBy",
                schema: "inventory",
                table: "PurchaseInvoices");

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_SourceId_Reason_RawMaterialId",
                schema: "inventory",
                table: "StockMovements",
                columns: new[] { "SourceId", "Reason", "RawMaterialId" },
                unique: true);
        }
    }
}
