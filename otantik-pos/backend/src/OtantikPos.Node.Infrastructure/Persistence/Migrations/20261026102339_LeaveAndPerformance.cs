using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OtantikPos.Node.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LeaveAndPerformance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AdministrativePenalties_EmployeeId_Date",
                schema: "hr",
                table: "AdministrativePenalties");

            migrationBuilder.AddColumn<decimal>(
                name: "Figures_AdministrativePenaltyAmounts",
                schema: "hr",
                table: "PayrollSlips",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "Figures_UnpaidLeaveDays",
                schema: "hr",
                table: "PayrollSlips",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "Figures_UnpaidLeaveDeduction",
                schema: "hr",
                table: "PayrollSlips",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<Guid>(
                name: "LeaveRequestId",
                schema: "hr",
                table: "DayMarks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "AdministrativePenaltyAmount",
                schema: "hr",
                table: "DailyRecords",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "Amount",
                schema: "hr",
                table: "AdministrativePenalties",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DisciplinaryActionId",
                schema: "hr",
                table: "AdministrativePenalties",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DisciplinaryActions",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    ViolationType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SeverityLevel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    PenaltyAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    IssuerId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    IssuerName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IssuedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsWithdrawn = table.Column<bool>(type: "boolean", nullable: false),
                    WithdrawnById = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    WithdrawnBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    WithdrawnAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    WithdrawalReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DisciplinaryActions", x => x.Id);
                    table.CheckConstraint("CK_DisciplinaryActions_Penalty", "(\"SeverityLevel\" = 'PenaltyDeduction') = (\"PenaltyAmount\" IS NOT NULL) AND (\"PenaltyAmount\" IS NULL OR \"PenaltyAmount\" > 0)");
                    table.CheckConstraint("CK_DisciplinaryActions_Withdrawal", "\"IsWithdrawn\" = (\"WithdrawnById\" IS NOT NULL AND \"WithdrawnAtUtc\" IS NOT NULL AND \"WithdrawalReason\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_DisciplinaryActions_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalSchema: "hr",
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LeaveTypes",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsPaid = table.Column<bool>(type: "boolean", nullable: false),
                    DefaultDaysPerYear = table.Column<int>(type: "integer", nullable: false),
                    ProratedForNewHires = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeaveTypes", x => x.Id);
                    table.CheckConstraint("CK_LeaveTypes_Days", "\"DefaultDaysPerYear\" BETWEEN 0 AND 365");
                    table.CheckConstraint("CK_LeaveTypes_Unpaid", "\"IsPaid\" OR \"DefaultDaysPerYear\" = 0");
                });

            migrationBuilder.CreateTable(
                name: "PerformanceEvaluations",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    TotalScore = table.Column<decimal>(type: "numeric(4,1)", precision: 4, scale: 1, nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ReviewerId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    ReviewerName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PerformanceEvaluations", x => x.Id);
                    table.CheckConstraint("CK_PerformanceEvaluations_Total", "\"TotalScore\" BETWEEN 20 AND 100");
                    table.ForeignKey(
                        name: "FK_PerformanceEvaluations_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalSchema: "hr",
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PerformanceKpis",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PerformanceKpis", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EmployeeLeaveBalances",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    LeaveTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: false),
                    TotalAllocatedDays = table.Column<decimal>(type: "numeric(4,1)", precision: 4, scale: 1, nullable: false),
                    UsedDays = table.Column<decimal>(type: "numeric(4,1)", precision: 4, scale: 1, nullable: false),
                    AllocatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AllocatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmployeeLeaveBalances", x => x.Id);
                    table.CheckConstraint("CK_EmployeeLeaveBalances_Days", "\"TotalAllocatedDays\" BETWEEN 0 AND 365 AND \"UsedDays\" BETWEEN 0 AND \"TotalAllocatedDays\"");
                    table.CheckConstraint("CK_EmployeeLeaveBalances_Year", "\"Year\" BETWEEN 2000 AND 2100");
                    table.ForeignKey(
                        name: "FK_EmployeeLeaveBalances_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalSchema: "hr",
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EmployeeLeaveBalances_LeaveTypes_LeaveTypeId",
                        column: x => x.LeaveTypeId,
                        principalSchema: "hr",
                        principalTable: "LeaveTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LeaveRequests",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    LeaveTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    TotalDaysRequested = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ManagerNotes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RequestedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RequestedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DecidedById = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    DecidedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DecidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelledFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    CancelledDays = table.Column<int>(type: "integer", nullable: false),
                    CancelledById = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CancelledBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CancelledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancellationNotes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeaveRequests", x => x.Id);
                    table.CheckConstraint("CK_LeaveRequests_Cancellation", "(\"CancelledFrom\" IS NULL) = (\"CancelledAtUtc\" IS NULL) AND (\"CancelledFrom\" IS NULL OR \"Status\" IN ('Approved', 'Cancelled')) AND (\"Status\" <> 'Cancelled' OR (\"CancelledFrom\" IS NOT NULL AND \"CancelledDays\" = \"TotalDaysRequested\")) AND \"CancelledDays\" BETWEEN 0 AND \"TotalDaysRequested\"");
                    table.CheckConstraint("CK_LeaveRequests_Dates", "\"EndDate\" >= \"StartDate\" AND date_part('year', \"EndDate\") = date_part('year', \"StartDate\") AND \"EndDate\" - \"StartDate\" < 90");
                    table.CheckConstraint("CK_LeaveRequests_Days", "\"TotalDaysRequested\" > 0 AND \"TotalDaysRequested\" <= \"EndDate\" - \"StartDate\" + 1");
                    table.CheckConstraint("CK_LeaveRequests_Decision", "(\"Status\" = 'Pending') = (\"DecidedById\" IS NULL AND \"DecidedAtUtc\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_LeaveRequests_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalSchema: "hr",
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaveRequests_LeaveTypes_LeaveTypeId",
                        column: x => x.LeaveTypeId,
                        principalSchema: "hr",
                        principalTable: "LeaveTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EvaluationScores",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    KpiId = table.Column<Guid>(type: "uuid", nullable: false),
                    KpiName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Score = table.Column<int>(type: "integer", nullable: false),
                    EvaluationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvaluationScores", x => x.Id);
                    table.CheckConstraint("CK_EvaluationScores_Score", "\"Score\" BETWEEN 1 AND 5");
                    table.ForeignKey(
                        name: "FK_EvaluationScores_PerformanceEvaluations_EvaluationId",
                        column: x => x.EvaluationId,
                        principalSchema: "hr",
                        principalTable: "PerformanceEvaluations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EvaluationScores_PerformanceKpis_KpiId",
                        column: x => x.KpiId,
                        principalSchema: "hr",
                        principalTable: "PerformanceKpis",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DayMarks_LeaveRequestId",
                schema: "hr",
                table: "DayMarks",
                column: "LeaveRequestId",
                filter: "\"LeaveRequestId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DayMarks_UnpaidLeave",
                schema: "hr",
                table: "DayMarks",
                sql: "\"Kind\" <> 'UnpaidLeave' OR \"LeaveRequestId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AdministrativePenalties_DisciplinaryActionId",
                schema: "hr",
                table: "AdministrativePenalties",
                column: "DisciplinaryActionId",
                unique: true,
                filter: "\"DisciplinaryActionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AdministrativePenalties_EmployeeId_Date",
                schema: "hr",
                table: "AdministrativePenalties",
                columns: new[] { "EmployeeId", "Date" },
                unique: true,
                filter: "\"DisciplinaryActionId\" IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AdministrativePenalties_Disciplinary",
                schema: "hr",
                table: "AdministrativePenalties",
                sql: "\"DisciplinaryActionId\" IS NULL OR \"Amount\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AdministrativePenalties_HoursOrAmount",
                schema: "hr",
                table: "AdministrativePenalties",
                sql: "(\"Hours\" > 0 AND \"Amount\" IS NULL) OR (\"Hours\" = 0 AND \"Amount\" > 0)");

            migrationBuilder.CreateIndex(
                name: "IX_DisciplinaryActions_EmployeeId_Date",
                schema: "hr",
                table: "DisciplinaryActions",
                columns: new[] { "EmployeeId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeLeaveBalances_EmployeeId_LeaveTypeId_Year",
                schema: "hr",
                table: "EmployeeLeaveBalances",
                columns: new[] { "EmployeeId", "LeaveTypeId", "Year" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeLeaveBalances_LeaveTypeId",
                schema: "hr",
                table: "EmployeeLeaveBalances",
                column: "LeaveTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeLeaveBalances_Year",
                schema: "hr",
                table: "EmployeeLeaveBalances",
                column: "Year");

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationScores_EvaluationId_KpiId",
                schema: "hr",
                table: "EvaluationScores",
                columns: new[] { "EvaluationId", "KpiId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationScores_KpiId",
                schema: "hr",
                table: "EvaluationScores",
                column: "KpiId");

            migrationBuilder.CreateIndex(
                name: "IX_LeaveRequests_EmployeeId_StartDate",
                schema: "hr",
                table: "LeaveRequests",
                columns: new[] { "EmployeeId", "StartDate" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaveRequests_LeaveTypeId",
                schema: "hr",
                table: "LeaveRequests",
                column: "LeaveTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_LeaveRequests_Status",
                schema: "hr",
                table: "LeaveRequests",
                column: "Status",
                filter: "\"Status\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_LeaveTypes_Name",
                schema: "hr",
                table: "LeaveTypes",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PerformanceEvaluations_EmployeeId_Date",
                schema: "hr",
                table: "PerformanceEvaluations",
                columns: new[] { "EmployeeId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_PerformanceKpis_Name",
                schema: "hr",
                table: "PerformanceKpis",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AdministrativePenalties_DisciplinaryActions_DisciplinaryAct~",
                schema: "hr",
                table: "AdministrativePenalties",
                column: "DisciplinaryActionId",
                principalSchema: "hr",
                principalTable: "DisciplinaryActions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_DayMarks_LeaveRequests_LeaveRequestId",
                schema: "hr",
                table: "DayMarks",
                column: "LeaveRequestId",
                principalSchema: "hr",
                principalTable: "LeaveRequests",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // The leave types to start with, changed or added to on the Leaves & Holidays tab: annual
            // leave (shared for new hires), sick leave, and unpaid leave.
            migrationBuilder.InsertData(
                schema: "hr",
                table: "LeaveTypes",
                columns: ["Id", "Name", "IsPaid", "DefaultDaysPerYear", "ProratedForNewHires", "CreatedAtUtc"],
                values: new object[,]
                {
                    { new Guid("6d0c5e1a-3f0b-4b5e-9a11-0a5e1a7e0001"), "Annual (اعتيادي)", true, 21, true, new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("6d0c5e1a-3f0b-4b5e-9a11-0a5e1a7e0002"), "Sick (مرضي)", true, 7, false, new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("6d0c5e1a-3f0b-4b5e-9a11-0a5e1a7e0003"), "Unpaid (بدون راتب)", false, 0, false, new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc) },
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AdministrativePenalties_DisciplinaryActions_DisciplinaryAct~",
                schema: "hr",
                table: "AdministrativePenalties");

            migrationBuilder.DropForeignKey(
                name: "FK_DayMarks_LeaveRequests_LeaveRequestId",
                schema: "hr",
                table: "DayMarks");

            migrationBuilder.DropTable(
                name: "DisciplinaryActions",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "EmployeeLeaveBalances",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "EvaluationScores",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "LeaveRequests",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "PerformanceEvaluations",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "PerformanceKpis",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "LeaveTypes",
                schema: "hr");

            migrationBuilder.DropIndex(
                name: "IX_DayMarks_LeaveRequestId",
                schema: "hr",
                table: "DayMarks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_DayMarks_UnpaidLeave",
                schema: "hr",
                table: "DayMarks");

            migrationBuilder.DropIndex(
                name: "IX_AdministrativePenalties_DisciplinaryActionId",
                schema: "hr",
                table: "AdministrativePenalties");

            migrationBuilder.DropIndex(
                name: "IX_AdministrativePenalties_EmployeeId_Date",
                schema: "hr",
                table: "AdministrativePenalties");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AdministrativePenalties_Disciplinary",
                schema: "hr",
                table: "AdministrativePenalties");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AdministrativePenalties_HoursOrAmount",
                schema: "hr",
                table: "AdministrativePenalties");

            migrationBuilder.DropColumn(
                name: "Figures_AdministrativePenaltyAmounts",
                schema: "hr",
                table: "PayrollSlips");

            migrationBuilder.DropColumn(
                name: "Figures_UnpaidLeaveDays",
                schema: "hr",
                table: "PayrollSlips");

            migrationBuilder.DropColumn(
                name: "Figures_UnpaidLeaveDeduction",
                schema: "hr",
                table: "PayrollSlips");

            migrationBuilder.DropColumn(
                name: "LeaveRequestId",
                schema: "hr",
                table: "DayMarks");

            migrationBuilder.DropColumn(
                name: "AdministrativePenaltyAmount",
                schema: "hr",
                table: "DailyRecords");

            migrationBuilder.DropColumn(
                name: "Amount",
                schema: "hr",
                table: "AdministrativePenalties");

            migrationBuilder.DropColumn(
                name: "DisciplinaryActionId",
                schema: "hr",
                table: "AdministrativePenalties");

            migrationBuilder.CreateIndex(
                name: "IX_AdministrativePenalties_EmployeeId_Date",
                schema: "hr",
                table: "AdministrativePenalties",
                columns: new[] { "EmployeeId", "Date" },
                unique: true);
        }
    }
}
