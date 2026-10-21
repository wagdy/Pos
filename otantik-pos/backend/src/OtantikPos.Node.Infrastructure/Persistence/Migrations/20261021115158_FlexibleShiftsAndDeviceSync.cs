using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OtantikPos.Node.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FlexibleShiftsAndDeviceSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<TimeOnly>(
                name: "ShiftStartsAt",
                schema: "hr",
                table: "Employees",
                type: "time without time zone",
                nullable: true,
                oldClrType: typeof(TimeOnly),
                oldType: "time without time zone");

            migrationBuilder.AddColumn<DateTime>(
                name: "LateSettledAtUtc",
                schema: "hr",
                table: "AttendanceLogs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LateSettledBy",
                schema: "hr",
                table: "AttendanceLogs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DeviceCommands",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceSerialNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Text = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    QueuedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    QueuedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AnsweredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReturnCode = table.Column<int>(type: "integer", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceCommands", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeviceUsers",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceSerialNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DeviceUserId = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FirstSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceUsers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceCommands_DeviceSerialNumber_Number",
                schema: "hr",
                table: "DeviceCommands",
                columns: new[] { "DeviceSerialNumber", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceUsers_DeviceSerialNumber_DeviceUserId",
                schema: "hr",
                table: "DeviceUsers",
                columns: new[] { "DeviceSerialNumber", "DeviceUserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceCommands",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "DeviceUsers",
                schema: "hr");

            migrationBuilder.DropColumn(
                name: "LateSettledAtUtc",
                schema: "hr",
                table: "AttendanceLogs");

            migrationBuilder.DropColumn(
                name: "LateSettledBy",
                schema: "hr",
                table: "AttendanceLogs");

            migrationBuilder.AlterColumn<TimeOnly>(
                name: "ShiftStartsAt",
                schema: "hr",
                table: "Employees",
                type: "time without time zone",
                nullable: false,
                defaultValue: new TimeOnly(0, 0, 0),
                oldClrType: typeof(TimeOnly),
                oldType: "time without time zone",
                oldNullable: true);
        }
    }
}
