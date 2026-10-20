using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OtantikPos.Node.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HrFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "hr");

            migrationBuilder.AddColumn<Guid>(
                name: "EmployeeId",
                schema: "inventory",
                table: "SafeTransactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "PayrollMonth",
                schema: "inventory",
                table: "SafeTransactions",
                type: "date",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AttendanceDevices",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SerialNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FirstSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ApprovedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AttendanceStamp = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendanceDevices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Employees",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    JobTitle = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    MonthlySalary = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    ShiftStartsAt = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    DaysOff = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DeviceUserId = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    StaffMemberId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    HiredOn = table.Column<DateOnly>(type: "date", nullable: false),
                    LeftOn = table.Column<DateOnly>(type: "date", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Employees", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    GraceMinutes = table.Column<int>(type: "integer", nullable: false),
                    DoubleScanMinutes = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AttendanceLogs",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DeviceSerialNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DeviceUserId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: true),
                    PunchedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    DeviceStatus = table.Column<int>(type: "integer", nullable: true),
                    VerifyMode = table.Column<int>(type: "integer", nullable: true),
                    ReceivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RecordedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    IgnoredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IgnoredBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IgnoreReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendanceLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AttendanceLogs_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalSchema: "hr",
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DailyRecords",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FirstIn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    LastOut = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Punches = table.Column<int>(type: "integer", nullable: false),
                    WorkedMinutes = table.Column<int>(type: "integer", nullable: false),
                    LateMinutes = table.Column<int>(type: "integer", nullable: false),
                    EarlyLeaveMinutes = table.Column<int>(type: "integer", nullable: false),
                    AwayMinutes = table.Column<int>(type: "integer", nullable: false),
                    OvertimeMinutes = table.Column<int>(type: "integer", nullable: false),
                    CalculatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DailyRecords_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalSchema: "hr",
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DayMarks",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Notes = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    MarkedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    MarkedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DayMarks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DayMarks_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalSchema: "hr",
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PayrollAdjustments",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Month = table.Column<DateOnly>(type: "date", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    Reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ForDay = table.Column<DateOnly>(type: "date", nullable: true),
                    RecordedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ApprovedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    VoidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VoidedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollAdjustments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PayrollAdjustments_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalSchema: "hr",
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PayrollSlips",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Month = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    NetPayable = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    CarriedForward = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    WagesCost = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    CalculatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinalizedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinalizedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    Figures_AbsencePenalty = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    Figures_AbsentDays = table.Column<int>(type: "integer", nullable: false),
                    Figures_Advances = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    Figures_BaseSalary = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    Figures_BroughtForward = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    Figures_DaysEmployed = table.Column<int>(type: "integer", nullable: false),
                    Figures_DaysInMonth = table.Column<int>(type: "integer", nullable: false),
                    Figures_DaysOffWorked = table.Column<int>(type: "integer", nullable: false),
                    Figures_DaysWorked = table.Column<int>(type: "integer", nullable: false),
                    Figures_Incentives = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    Figures_LatenessPenalty = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    Figures_LeaveAllowances = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    Figures_MissingPunchDays = table.Column<int>(type: "integer", nullable: false),
                    Figures_MonthlySalary = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    Figures_OpenDays = table.Column<int>(type: "integer", nullable: false),
                    Figures_OvertimeMinutes = table.Column<int>(type: "integer", nullable: false),
                    Figures_OvertimePay = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    Figures_ShortMinutes = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollSlips", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PayrollSlips_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalSchema: "hr",
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SafeTransactions_EmployeeId_PayrollMonth",
                schema: "inventory",
                table: "SafeTransactions",
                columns: new[] { "EmployeeId", "PayrollMonth" },
                filter: "\"EmployeeId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceDevices_SerialNumber",
                schema: "hr",
                table: "AttendanceDevices",
                column: "SerialNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceLogs_DeviceSerialNumber_DeviceUserId_PunchedAt",
                schema: "hr",
                table: "AttendanceLogs",
                columns: new[] { "DeviceSerialNumber", "DeviceUserId", "PunchedAt" },
                unique: true,
                filter: "\"DeviceSerialNumber\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceLogs_DeviceUserId",
                schema: "hr",
                table: "AttendanceLogs",
                column: "DeviceUserId",
                filter: "\"EmployeeId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceLogs_EmployeeId_PunchedAt",
                schema: "hr",
                table: "AttendanceLogs",
                columns: new[] { "EmployeeId", "PunchedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DailyRecords_EmployeeId_Date",
                schema: "hr",
                table: "DailyRecords",
                columns: new[] { "EmployeeId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DayMarks_EmployeeId_Date",
                schema: "hr",
                table: "DayMarks",
                columns: new[] { "EmployeeId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Employees_DeviceUserId",
                schema: "hr",
                table: "Employees",
                column: "DeviceUserId",
                unique: true,
                filter: "\"DeviceUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Employees_StaffMemberId",
                schema: "hr",
                table: "Employees",
                column: "StaffMemberId",
                unique: true,
                filter: "\"StaffMemberId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PayrollAdjustments_EmployeeId_Month",
                schema: "hr",
                table: "PayrollAdjustments",
                columns: new[] { "EmployeeId", "Month" });

            migrationBuilder.CreateIndex(
                name: "IX_PayrollSlips_EmployeeId_Month",
                schema: "hr",
                table: "PayrollSlips",
                columns: new[] { "EmployeeId", "Month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PayrollSlips_Month",
                schema: "hr",
                table: "PayrollSlips",
                column: "Month");

            migrationBuilder.AddForeignKey(
                name: "FK_SafeTransactions_Employees_EmployeeId",
                schema: "inventory",
                table: "SafeTransactions",
                column: "EmployeeId",
                principalSchema: "hr",
                principalTable: "Employees",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SafeTransactions_Employees_EmployeeId",
                schema: "inventory",
                table: "SafeTransactions");

            migrationBuilder.DropTable(
                name: "AttendanceDevices",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "AttendanceLogs",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "DailyRecords",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "DayMarks",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "PayrollAdjustments",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "PayrollSlips",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "Settings",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "Employees",
                schema: "hr");

            migrationBuilder.DropIndex(
                name: "IX_SafeTransactions_EmployeeId_PayrollMonth",
                schema: "inventory",
                table: "SafeTransactions");

            migrationBuilder.DropColumn(
                name: "EmployeeId",
                schema: "inventory",
                table: "SafeTransactions");

            migrationBuilder.DropColumn(
                name: "PayrollMonth",
                schema: "inventory",
                table: "SafeTransactions");
        }
    }
}
