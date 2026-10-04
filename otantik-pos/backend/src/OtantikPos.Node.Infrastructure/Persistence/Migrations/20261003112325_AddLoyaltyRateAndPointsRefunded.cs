using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OtantikPos.Node.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLoyaltyRateAndPointsRefunded : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "RedemptionValuePer100Points",
                schema: "catalog",
                table: "RestaurantSettings",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: false,
                // The existing row predates the setting: Points / 10, the delivery system's own
                // default, until the next sync brings the real rate. Not 0, which would switch
                // redemption off in the meantime.
                defaultValue: 10m);

            migrationBuilder.AddColumn<int>(
                name: "PointsRefunded",
                schema: "pos",
                table: "Orders",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RedemptionValuePer100Points",
                schema: "catalog",
                table: "RestaurantSettings");

            migrationBuilder.DropColumn(
                name: "PointsRefunded",
                schema: "pos",
                table: "Orders");
        }
    }
}
