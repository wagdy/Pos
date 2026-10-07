using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OtantikPos.Node.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOnlineSales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OnlineSales",
                schema: "pos",
                columns: table => new
                {
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    SoldAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PaymentMethod = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DiscountAmount = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    DeliveryFee = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnlineSales", x => x.PublicId);
                });

            migrationBuilder.CreateTable(
                name: "OnlineSaleLines",
                schema: "pos",
                columns: table => new
                {
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    OnlineSalePublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    MenuItemId = table.Column<int>(type: "integer", nullable: false),
                    VariantId = table.Column<int>(type: "integer", nullable: true),
                    MenuItemName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    VariantName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    LineTotal = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    AddOnIds = table.Column<List<int>>(type: "integer[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnlineSaleLines", x => x.PublicId);
                    table.ForeignKey(
                        name: "FK_OnlineSaleLines_OnlineSales_OnlineSalePublicId",
                        column: x => x.OnlineSalePublicId,
                        principalSchema: "pos",
                        principalTable: "OnlineSales",
                        principalColumn: "PublicId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OnlineSaleLines_OnlineSalePublicId",
                schema: "pos",
                table: "OnlineSaleLines",
                column: "OnlineSalePublicId");

            migrationBuilder.CreateIndex(
                name: "IX_OnlineSales_SoldAtUtc",
                schema: "pos",
                table: "OnlineSales",
                column: "SoldAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OnlineSaleLines",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "OnlineSales",
                schema: "pos");
        }
    }
}
