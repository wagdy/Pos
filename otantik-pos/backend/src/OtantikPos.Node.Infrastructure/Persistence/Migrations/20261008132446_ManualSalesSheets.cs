using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OtantikPos.Node.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ManualSalesSheets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ManualSalesSheets",
                schema: "inventory",
                columns: table => new
                {
                    Year = table.Column<int>(type: "integer", nullable: false),
                    Month = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManualSalesSheets", x => new { x.Year, x.Month });
                });

            migrationBuilder.CreateTable(
                name: "ManualSalesLines",
                schema: "inventory",
                columns: table => new
                {
                    Year = table.Column<int>(type: "integer", nullable: false),
                    Month = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManualSalesLines", x => new { x.Year, x.Month, x.Kind, x.ItemId });
                    table.ForeignKey(
                        name: "FK_ManualSalesLines_ManualSalesSheets_Year_Month",
                        columns: x => new { x.Year, x.Month },
                        principalSchema: "inventory",
                        principalTable: "ManualSalesSheets",
                        principalColumns: new[] { "Year", "Month" },
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ManualSalesLines",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "ManualSalesSheets",
                schema: "inventory");
        }
    }
}
