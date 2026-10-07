using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OtantikPos.Node.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStockCountsAndSpoilage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Note",
                schema: "inventory",
                table: "StockMovements",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecordedBy",
                schema: "inventory",
                table: "StockMovements",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "VarianceTolerancePercent",
                schema: "inventory",
                table: "CostingSettings",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 5m);

            migrationBuilder.CreateTable(
                name: "StockCounts",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PostedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PostedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockCounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StockCountLines",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StockCountId = table.Column<Guid>(type: "uuid", nullable: false),
                    RawMaterialId = table.Column<Guid>(type: "uuid", nullable: false),
                    CountedQuantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    BookQuantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: true),
                    UnitCost = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockCountLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StockCountLines_RawMaterials_RawMaterialId",
                        column: x => x.RawMaterialId,
                        principalSchema: "inventory",
                        principalTable: "RawMaterials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockCountLines_StockCounts_StockCountId",
                        column: x => x.StockCountId,
                        principalSchema: "inventory",
                        principalTable: "StockCounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockCountLines_RawMaterialId",
                schema: "inventory",
                table: "StockCountLines",
                column: "RawMaterialId");

            migrationBuilder.CreateIndex(
                name: "IX_StockCountLines_StockCountId_RawMaterialId",
                schema: "inventory",
                table: "StockCountLines",
                columns: new[] { "StockCountId", "RawMaterialId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockCounts_Status",
                schema: "inventory",
                table: "StockCounts",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StockCountLines",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "StockCounts",
                schema: "inventory");

            migrationBuilder.DropColumn(
                name: "Note",
                schema: "inventory",
                table: "StockMovements");

            migrationBuilder.DropColumn(
                name: "RecordedBy",
                schema: "inventory",
                table: "StockMovements");

            migrationBuilder.DropColumn(
                name: "VarianceTolerancePercent",
                schema: "inventory",
                table: "CostingSettings");
        }
    }
}
