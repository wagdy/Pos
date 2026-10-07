using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OtantikPos.Node.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCosting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "QuantityPerPortion",
                schema: "inventory",
                table: "RecipeIngredients",
                newName: "Quantity");

            migrationBuilder.AddColumn<decimal>(
                name: "UnitCost",
                schema: "inventory",
                table: "StockMovements",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Portions",
                schema: "inventory",
                table: "Recipes",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<decimal>(
                name: "YieldPercent",
                schema: "inventory",
                table: "RecipeIngredients",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 100m);

            migrationBuilder.AddColumn<decimal>(
                name: "AverageCost",
                schema: "inventory",
                table: "RawMaterials",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Category",
                schema: "inventory",
                table: "RawMaterials",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Code",
                schema: "inventory",
                table: "RawMaterials",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DefaultYieldPercent",
                schema: "inventory",
                table: "RawMaterials",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 100m);

            migrationBuilder.AddColumn<string>(
                name: "PurchaseUnit",
                schema: "inventory",
                table: "RawMaterials",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "PurchaseUnitSize",
                schema: "inventory",
                table: "RawMaterials",
                type: "numeric(18,3)",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            // Materials made before costing: bought by the kg, the litre or the piece, as a new one
            // starts. Without this their purchase unit would be empty and hold nothing.
            migrationBuilder.Sql("""
                UPDATE inventory."RawMaterials" SET
                    "PurchaseUnit" = CASE "Unit" WHEN 'Gram' THEN 'kg' WHEN 'Millilitre' THEN 'L' ELSE 'piece' END,
                    "PurchaseUnitSize" = CASE "Unit" WHEN 'Gram' THEN 1000 WHEN 'Millilitre' THEN 1000 ELSE 1 END
                """);

            migrationBuilder.CreateTable(
                name: "CostingSettings",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    FoodCostTargetPercent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CostingSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SharedCosts",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RawMaterialId = table.Column<Guid>(type: "uuid", nullable: true),
                    MonthlyAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharedCosts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SharedCosts_RawMaterials_RawMaterialId",
                        column: x => x.RawMaterialId,
                        principalSchema: "inventory",
                        principalTable: "RawMaterials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SharedCosts_RawMaterialId",
                schema: "inventory",
                table: "SharedCosts",
                column: "RawMaterialId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CostingSettings",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "SharedCosts",
                schema: "inventory");

            migrationBuilder.DropColumn(
                name: "UnitCost",
                schema: "inventory",
                table: "StockMovements");

            migrationBuilder.DropColumn(
                name: "Portions",
                schema: "inventory",
                table: "Recipes");

            migrationBuilder.DropColumn(
                name: "YieldPercent",
                schema: "inventory",
                table: "RecipeIngredients");

            migrationBuilder.DropColumn(
                name: "AverageCost",
                schema: "inventory",
                table: "RawMaterials");

            migrationBuilder.DropColumn(
                name: "Category",
                schema: "inventory",
                table: "RawMaterials");

            migrationBuilder.DropColumn(
                name: "Code",
                schema: "inventory",
                table: "RawMaterials");

            migrationBuilder.DropColumn(
                name: "DefaultYieldPercent",
                schema: "inventory",
                table: "RawMaterials");

            migrationBuilder.DropColumn(
                name: "PurchaseUnit",
                schema: "inventory",
                table: "RawMaterials");

            migrationBuilder.DropColumn(
                name: "PurchaseUnitSize",
                schema: "inventory",
                table: "RawMaterials");

            migrationBuilder.RenameColumn(
                name: "Quantity",
                schema: "inventory",
                table: "RecipeIngredients",
                newName: "QuantityPerPortion");
        }
    }
}
