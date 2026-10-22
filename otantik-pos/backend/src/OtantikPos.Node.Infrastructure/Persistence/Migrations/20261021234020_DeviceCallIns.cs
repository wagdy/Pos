using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OtantikPos.Node.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeviceCallIns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastCalledInAtUtc",
                schema: "hr",
                table: "AttendanceDevices",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastCalledInAtUtc",
                schema: "hr",
                table: "AttendanceDevices");
        }
    }
}
