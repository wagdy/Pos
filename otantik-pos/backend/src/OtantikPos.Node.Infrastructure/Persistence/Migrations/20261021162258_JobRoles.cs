using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OtantikPos.Node.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class JobRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "JobRoleId",
                schema: "hr",
                table: "Employees",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "JobRoles",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobRoles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobRoles_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalSchema: "hr",
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Employees_JobRoleId",
                schema: "hr",
                table: "Employees",
                column: "JobRoleId");

            migrationBuilder.CreateIndex(
                name: "IX_JobRoles_DepartmentId_Name",
                schema: "hr",
                table: "JobRoles",
                columns: new[] { "DepartmentId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Employees_JobRoles_JobRoleId",
                schema: "hr",
                table: "Employees",
                column: "JobRoleId",
                principalSchema: "hr",
                principalTable: "JobRoles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // The job titles typed so far become their departments' roles, one to a name whatever
            // its case and spaces, and each employee holds theirs.
            migrationBuilder.Sql("""
                INSERT INTO hr."JobRoles" ("Id", "DepartmentId", "Name", "CreatedAtUtc")
                SELECT gen_random_uuid(), "DepartmentId", min(btrim("JobTitle")), now()
                FROM hr."Employees"
                WHERE btrim("JobTitle") <> ''
                GROUP BY "DepartmentId", lower(btrim("JobTitle"));

                UPDATE hr."Employees" e
                SET "JobRoleId" = r."Id", "JobTitle" = r."Name"
                FROM hr."JobRoles" r
                WHERE r."DepartmentId" = e."DepartmentId" AND lower(r."Name") = lower(btrim(e."JobTitle"));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Employees_JobRoles_JobRoleId",
                schema: "hr",
                table: "Employees");

            migrationBuilder.DropTable(
                name: "JobRoles",
                schema: "hr");

            migrationBuilder.DropIndex(
                name: "IX_Employees_JobRoleId",
                schema: "hr",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "JobRoleId",
                schema: "hr",
                table: "Employees");
        }
    }
}
