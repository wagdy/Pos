using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OtantikPos.Node.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PublicHolidays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Figures_HolidaysWorked",
                schema: "hr",
                table: "PayrollSlips",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "Figures_HolidaysWorkedPay",
                schema: "hr",
                table: "PayrollSlips",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "PublicHolidays",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FirstDay = table.Column<DateOnly>(type: "date", nullable: false),
                    LastDay = table.Column<DateOnly>(type: "date", nullable: false),
                    SetBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SetAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicHolidays", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PublicHolidays_FirstDay_LastDay",
                schema: "hr",
                table: "PublicHolidays",
                columns: new[] { "FirstDay", "LastDay" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PublicHolidays",
                schema: "hr");

            migrationBuilder.DropColumn(
                name: "Figures_HolidaysWorked",
                schema: "hr",
                table: "PayrollSlips");

            migrationBuilder.DropColumn(
                name: "Figures_HolidaysWorkedPay",
                schema: "hr",
                table: "PayrollSlips");
        }
    }
}
