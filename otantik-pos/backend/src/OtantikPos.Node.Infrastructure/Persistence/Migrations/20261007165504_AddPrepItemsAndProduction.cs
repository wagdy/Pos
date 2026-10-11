using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OtantikPos.Node.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPrepItemsAndProduction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                schema: "inventory",
                table: "RawMaterials",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                // Every material so far was bought.
                defaultValue: "Purchased");

            migrationBuilder.CreateTable(
                name: "PrepRecipes",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchQuantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrepRecipes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrepRecipes_RawMaterials_Id",
                        column: x => x.Id,
                        principalSchema: "inventory",
                        principalTable: "RawMaterials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProductionOrders",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PrepItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlannedQuantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    ProducedQuantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    TotalCost = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    UnitCost = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    ProducedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ProducedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Notes = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionOrders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductionOrders_RawMaterials_PrepItemId",
                        column: x => x.PrepItemId,
                        principalSchema: "inventory",
                        principalTable: "RawMaterials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PrepRecipeIngredients",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PrepRecipeId = table.Column<Guid>(type: "uuid", nullable: false),
                    RawMaterialId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    YieldPercent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrepRecipeIngredients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrepRecipeIngredients_PrepRecipes_PrepRecipeId",
                        column: x => x.PrepRecipeId,
                        principalSchema: "inventory",
                        principalTable: "PrepRecipes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PrepRecipeIngredients_RawMaterials_RawMaterialId",
                        column: x => x.RawMaterialId,
                        principalSchema: "inventory",
                        principalTable: "RawMaterials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductionOrderLines",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductionOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    RawMaterialId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    UnitCost = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    Cost = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionOrderLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductionOrderLines_ProductionOrders_ProductionOrderId",
                        column: x => x.ProductionOrderId,
                        principalSchema: "inventory",
                        principalTable: "ProductionOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProductionOrderLines_RawMaterials_RawMaterialId",
                        column: x => x.RawMaterialId,
                        principalSchema: "inventory",
                        principalTable: "RawMaterials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PrepRecipeIngredients_PrepRecipeId_RawMaterialId",
                schema: "inventory",
                table: "PrepRecipeIngredients",
                columns: new[] { "PrepRecipeId", "RawMaterialId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrepRecipeIngredients_RawMaterialId",
                schema: "inventory",
                table: "PrepRecipeIngredients",
                column: "RawMaterialId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionOrderLines_ProductionOrderId_RawMaterialId",
                schema: "inventory",
                table: "ProductionOrderLines",
                columns: new[] { "ProductionOrderId", "RawMaterialId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductionOrderLines_RawMaterialId",
                schema: "inventory",
                table: "ProductionOrderLines",
                column: "RawMaterialId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionOrders_PrepItemId_ProducedAtUtc",
                schema: "inventory",
                table: "ProductionOrders",
                columns: new[] { "PrepItemId", "ProducedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductionOrders_ProducedAtUtc",
                schema: "inventory",
                table: "ProductionOrders",
                column: "ProducedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PrepRecipeIngredients",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "ProductionOrderLines",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "PrepRecipes",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "ProductionOrders",
                schema: "inventory");

            migrationBuilder.DropColumn(
                name: "Kind",
                schema: "inventory",
                table: "RawMaterials");
        }
    }
}
