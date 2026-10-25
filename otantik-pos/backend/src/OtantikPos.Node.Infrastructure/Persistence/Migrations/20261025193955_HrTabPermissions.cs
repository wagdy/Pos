using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OtantikPos.Node.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HrTabPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HrTabs",
                schema: "hr",
                table: "Employees",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                // Everyone there now has no HR tabs: "None", read back as no tab.
                defaultValue: "None");

            migrationBuilder.CreateTable(
                name: "StaffHrAccess",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StaffId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Tabs = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SetBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SetAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffHrAccess", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StaffHrAccess_StaffId",
                schema: "hr",
                table: "StaffHrAccess",
                column: "StaffId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StaffHrAccess",
                schema: "hr");

            migrationBuilder.DropColumn(
                name: "HrTabs",
                schema: "hr",
                table: "Employees");
        }
    }
}
