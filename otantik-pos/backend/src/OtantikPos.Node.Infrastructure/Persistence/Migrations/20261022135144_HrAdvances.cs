using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OtantikPos.Node.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HrAdvances : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "SourceTransactionId",
                schema: "hr",
                table: "AdvanceAllocations",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "SourceAdvanceId",
                schema: "hr",
                table: "AdvanceAllocations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HrAdvances",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: true),
                    PayrollMonth = table.Column<DateOnly>(type: "date", nullable: true),
                    Notes = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    RecordedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ApprovedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    VoidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VoidedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HrAdvances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HrAdvances_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalSchema: "hr",
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdvanceAllocations_SourceAdvanceId",
                schema: "hr",
                table: "AdvanceAllocations",
                column: "SourceAdvanceId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AdvanceAllocations_OneSource",
                schema: "hr",
                table: "AdvanceAllocations",
                sql: "(\"SourceTransactionId\" IS NULL) <> (\"SourceAdvanceId\" IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_HrAdvances_EmployeeId_PayrollMonth",
                schema: "hr",
                table: "HrAdvances",
                columns: new[] { "EmployeeId", "PayrollMonth" },
                filter: "\"EmployeeId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_AdvanceAllocations_HrAdvances_SourceAdvanceId",
                schema: "hr",
                table: "AdvanceAllocations",
                column: "SourceAdvanceId",
                principalSchema: "hr",
                principalTable: "HrAdvances",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AdvanceAllocations_HrAdvances_SourceAdvanceId",
                schema: "hr",
                table: "AdvanceAllocations");

            migrationBuilder.DropTable(
                name: "HrAdvances",
                schema: "hr");

            migrationBuilder.DropIndex(
                name: "IX_AdvanceAllocations_SourceAdvanceId",
                schema: "hr",
                table: "AdvanceAllocations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AdvanceAllocations_OneSource",
                schema: "hr",
                table: "AdvanceAllocations");

            migrationBuilder.DropColumn(
                name: "SourceAdvanceId",
                schema: "hr",
                table: "AdvanceAllocations");

            migrationBuilder.AlterColumn<Guid>(
                name: "SourceTransactionId",
                schema: "hr",
                table: "AdvanceAllocations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
