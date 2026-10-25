using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OtantikPos.Node.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CustodyAndAssets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AssetCategoryId",
                schema: "inventory",
                table: "ExpenseItems",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FailedPinAttempts",
                schema: "hr",
                table: "Employees",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PinHash",
                schema: "hr",
                table: "Employees",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PinLockedUntilUtc",
                schema: "hr",
                table: "Employees",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PinSetAtUtc",
                schema: "hr",
                table: "Employees",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AssetCategories",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsConsumable = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetCategories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AssetItems",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    UnitCost = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: true),
                    Origin = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SourceInvoiceId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceInvoiceLineId = table.Column<Guid>(type: "uuid", nullable: true),
                    Notes = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetItems", x => x.Id);
                    table.CheckConstraint("CK_AssetItems_Quantity", "\"Quantity\" >= 0");
                    table.CheckConstraint("CK_AssetItems_Source", "(\"Origin\" = 'FinancialPurchase') = (\"SourceInvoiceId\" IS NOT NULL AND \"SourceInvoiceLineId\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_AssetItems_AssetCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalSchema: "hr",
                        principalTable: "AssetCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetItems_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalSchema: "hr",
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetItems_PurchaseInvoices_SourceInvoiceId",
                        column: x => x.SourceInvoiceId,
                        principalSchema: "inventory",
                        principalTable: "PurchaseInvoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AssetTransactions",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: true),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Notes = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    UnitCost = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    RecordedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    WasteEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetTransactions", x => x.Id);
                    table.CheckConstraint("CK_AssetTransactions_Quantity", "\"Quantity\" > 0");
                    table.CheckConstraint("CK_AssetTransactions_Reason", "(\"Type\" = 'CheckOut') = (\"Reason\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_AssetTransactions_AssetItems_AssetItemId",
                        column: x => x.AssetItemId,
                        principalSchema: "hr",
                        principalTable: "AssetItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetTransactions_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalSchema: "hr",
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BreakageEntries",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessDate = table.Column<DateOnly>(type: "date", nullable: false),
                    AssetItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetTransactionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Description = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    UnitCost = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Cost = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: true),
                    Notes = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    RecordedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    VoidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VoidedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BreakageEntries", x => x.Id);
                    table.CheckConstraint("CK_BreakageEntries_Quantity", "\"Quantity\" > 0");
                    table.ForeignKey(
                        name: "FK_BreakageEntries_AssetItems_AssetItemId",
                        column: x => x.AssetItemId,
                        principalSchema: "hr",
                        principalTable: "AssetItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BreakageEntries_AssetTransactions_AssetTransactionId",
                        column: x => x.AssetTransactionId,
                        principalSchema: "hr",
                        principalTable: "AssetTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BreakageEntries_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalSchema: "hr",
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseItems_AssetCategoryId",
                schema: "inventory",
                table: "ExpenseItems",
                column: "AssetCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Employees_MobileNumber",
                schema: "hr",
                table: "Employees",
                column: "MobileNumber",
                unique: true,
                filter: "\"MobileNumber\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AssetCategories_Name",
                schema: "hr",
                table: "AssetCategories",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssetItems_CategoryId",
                schema: "hr",
                table: "AssetItems",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetItems_EmployeeId",
                schema: "hr",
                table: "AssetItems",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetItems_SourceInvoiceId",
                schema: "hr",
                table: "AssetItems",
                column: "SourceInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetItems_SourceInvoiceLineId",
                schema: "hr",
                table: "AssetItems",
                column: "SourceInvoiceLineId",
                unique: true,
                filter: "\"SourceInvoiceLineId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AssetTransactions_AssetItemId_Date",
                schema: "hr",
                table: "AssetTransactions",
                columns: new[] { "AssetItemId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_AssetTransactions_Date",
                schema: "hr",
                table: "AssetTransactions",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_AssetTransactions_EmployeeId",
                schema: "hr",
                table: "AssetTransactions",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetTransactions_WasteEntryId",
                schema: "hr",
                table: "AssetTransactions",
                column: "WasteEntryId",
                unique: true,
                filter: "\"WasteEntryId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BreakageEntries_AssetItemId",
                schema: "inventory",
                table: "BreakageEntries",
                column: "AssetItemId");

            migrationBuilder.CreateIndex(
                name: "IX_BreakageEntries_AssetTransactionId",
                schema: "inventory",
                table: "BreakageEntries",
                column: "AssetTransactionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BreakageEntries_BusinessDate",
                schema: "inventory",
                table: "BreakageEntries",
                column: "BusinessDate");

            migrationBuilder.CreateIndex(
                name: "IX_BreakageEntries_EmployeeId",
                schema: "inventory",
                table: "BreakageEntries",
                column: "EmployeeId");

            migrationBuilder.AddForeignKey(
                name: "FK_ExpenseItems_AssetCategories_AssetCategoryId",
                schema: "inventory",
                table: "ExpenseItems",
                column: "AssetCategoryId",
                principalSchema: "hr",
                principalTable: "AssetCategories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ExpenseItems_AssetCategories_AssetCategoryId",
                schema: "inventory",
                table: "ExpenseItems");

            migrationBuilder.DropTable(
                name: "BreakageEntries",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "AssetTransactions",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "AssetItems",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "AssetCategories",
                schema: "hr");

            migrationBuilder.DropIndex(
                name: "IX_ExpenseItems_AssetCategoryId",
                schema: "inventory",
                table: "ExpenseItems");

            migrationBuilder.DropIndex(
                name: "IX_Employees_MobileNumber",
                schema: "hr",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "AssetCategoryId",
                schema: "inventory",
                table: "ExpenseItems");

            migrationBuilder.DropColumn(
                name: "FailedPinAttempts",
                schema: "hr",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "PinHash",
                schema: "hr",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "PinLockedUntilUtc",
                schema: "hr",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "PinSetAtUtc",
                schema: "hr",
                table: "Employees");
        }
    }
}
