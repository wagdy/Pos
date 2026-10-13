using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OtantikPos.Node.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OnlineSaleCharged : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "PointsDiscountAmount",
                schema: "pos",
                table: "OnlineSales",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxAmount",
                schema: "pos",
                table: "OnlineSales",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TotalAmount",
                schema: "pos",
                table: "OnlineSales",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            // Sales recorded before the charge was kept: what they were counted as until now, their
            // items less the discount plus the delivery fee, with no VAT or points known.
            migrationBuilder.Sql("""
                UPDATE pos."OnlineSales" s
                SET "TotalAmount" = COALESCE((SELECT SUM(l."LineTotal") FROM pos."OnlineSaleLines" l WHERE l."OnlineSalePublicId" = s."PublicId"), 0)
                    - s."DiscountAmount" + s."DeliveryFee";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PointsDiscountAmount",
                schema: "pos",
                table: "OnlineSales");

            migrationBuilder.DropColumn(
                name: "TaxAmount",
                schema: "pos",
                table: "OnlineSales");

            migrationBuilder.DropColumn(
                name: "TotalAmount",
                schema: "pos",
                table: "OnlineSales");
        }
    }
}
