using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OtantikPos.Node.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdministrativePenalties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // "Missing a punch" is now "Suspended": a day kept as a finalized slip counted it reads
            // as it is called now.
            migrationBuilder.Sql("""UPDATE hr."DailyRecords" SET "Status" = 'Suspended' WHERE "Status" = 'MissingPunch';""");

            migrationBuilder.AddColumn<decimal>(
                name: "Figures_AdministrativePenalty",
                schema: "hr",
                table: "PayrollSlips",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "Figures_AdministrativePenaltyHours",
                schema: "hr",
                table: "PayrollSlips",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "AdministrativePenaltyHours",
                schema: "hr",
                table: "DailyRecords",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "AdministrativePenalties",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Hours = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    Reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SetBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SetAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdministrativePenalties", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdministrativePenalties_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalSchema: "hr",
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdministrativePenalties_EmployeeId_Date",
                schema: "hr",
                table: "AdministrativePenalties",
                columns: new[] { "EmployeeId", "Date" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""UPDATE hr."DailyRecords" SET "Status" = 'MissingPunch' WHERE "Status" = 'Suspended';""");

            migrationBuilder.DropTable(
                name: "AdministrativePenalties",
                schema: "hr");

            migrationBuilder.DropColumn(
                name: "Figures_AdministrativePenalty",
                schema: "hr",
                table: "PayrollSlips");

            migrationBuilder.DropColumn(
                name: "Figures_AdministrativePenaltyHours",
                schema: "hr",
                table: "PayrollSlips");

            migrationBuilder.DropColumn(
                name: "AdministrativePenaltyHours",
                schema: "hr",
                table: "DailyRecords");
        }
    }
}
