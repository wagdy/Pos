using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OtantikPos.Node.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EmployeeProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "IdPhotoBytes",
                schema: "hr",
                table: "Employees",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdPhotoContentType",
                schema: "hr",
                table: "Employees",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdPhotoFileName",
                schema: "hr",
                table: "Employees",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "IdPhotoUploadedAtUtc",
                schema: "hr",
                table: "Employees",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdPhotoUploadedBy",
                schema: "hr",
                table: "Employees",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MobileNumber",
                schema: "hr",
                table: "Employees",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NationalId",
                schema: "hr",
                table: "Employees",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EmployeeIdPhotos",
                schema: "hr",
                columns: table => new
                {
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Content = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmployeeIdPhotos", x => x.EmployeeId);
                    table.ForeignKey(
                        name: "FK_EmployeeIdPhotos_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalSchema: "hr",
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Employees_NationalId",
                schema: "hr",
                table: "Employees",
                column: "NationalId",
                unique: true,
                filter: "\"NationalId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmployeeIdPhotos",
                schema: "hr");

            migrationBuilder.DropIndex(
                name: "IX_Employees_NationalId",
                schema: "hr",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "IdPhotoBytes",
                schema: "hr",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "IdPhotoContentType",
                schema: "hr",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "IdPhotoFileName",
                schema: "hr",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "IdPhotoUploadedAtUtc",
                schema: "hr",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "IdPhotoUploadedBy",
                schema: "hr",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "MobileNumber",
                schema: "hr",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "NationalId",
                schema: "hr",
                table: "Employees");
        }
    }
}
