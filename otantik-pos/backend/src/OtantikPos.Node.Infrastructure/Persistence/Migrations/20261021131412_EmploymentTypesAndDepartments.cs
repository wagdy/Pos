using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OtantikPos.Node.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EmploymentTypesAndDepartments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Figures_DailyWage",
                schema: "hr",
                table: "PayrollSlips",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "Figures_EmploymentType",
                schema: "hr",
                table: "PayrollSlips",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                // Every slip so far was a monthly salary's.
                defaultValue: "Monthly");

            migrationBuilder.AddColumn<decimal>(
                name: "DailyWage",
                schema: "hr",
                table: "Employees",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<Guid>(
                name: "DepartmentId",
                schema: "hr",
                table: "Employees",
                type: "uuid",
                nullable: false,
                // The employees already entered start in General, made below.
                defaultValue: new Guid("7a1e3c4d-0000-4000-8000-000000000001"));

            migrationBuilder.AddColumn<string>(
                name: "EmploymentType",
                schema: "hr",
                table: "Employees",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                // Everyone so far had a monthly salary.
                defaultValue: "Monthly");

            migrationBuilder.CreateTable(
                name: "Departments",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Departments", x => x.Id);
                });

            migrationBuilder.InsertData(
                schema: "hr",
                table: "Departments",
                columns: new[] { "Id", "CreatedAtUtc", "Name" },
                values: new object[] { new Guid("7a1e3c4d-0000-4000-8000-000000000001"), new DateTime(2026, 10, 21, 0, 0, 0, 0, DateTimeKind.Utc), "General" });

            migrationBuilder.CreateIndex(
                name: "IX_Employees_DepartmentId",
                schema: "hr",
                table: "Employees",
                column: "DepartmentId");

            migrationBuilder.AddForeignKey(
                name: "FK_Employees_Departments_DepartmentId",
                schema: "hr",
                table: "Employees",
                column: "DepartmentId",
                principalSchema: "hr",
                principalTable: "Departments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Employees_Departments_DepartmentId",
                schema: "hr",
                table: "Employees");

            migrationBuilder.DropTable(
                name: "Departments",
                schema: "hr");

            migrationBuilder.DropIndex(
                name: "IX_Employees_DepartmentId",
                schema: "hr",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "Figures_DailyWage",
                schema: "hr",
                table: "PayrollSlips");

            migrationBuilder.DropColumn(
                name: "Figures_EmploymentType",
                schema: "hr",
                table: "PayrollSlips");

            migrationBuilder.DropColumn(
                name: "DailyWage",
                schema: "hr",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "DepartmentId",
                schema: "hr",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "EmploymentType",
                schema: "hr",
                table: "Employees");
        }
    }
}
