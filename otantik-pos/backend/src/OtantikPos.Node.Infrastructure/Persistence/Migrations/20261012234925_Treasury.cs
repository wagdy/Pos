using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OtantikPos.Node.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The treasury: the safe's ledger (SafeTransactions), each supplier's account
    /// (SupplierLedgerEntries), and how a purchase invoice is paid. Invoices posted before it are
    /// marked as such, and touch neither the safe nor any supplier's account.
    /// </summary>
    public partial class Treasury : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PaymentMethod",
                schema: "inventory",
                table: "PurchaseInvoices",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "RevenueDate",
                schema: "inventory",
                table: "PurchaseInvoices",
                type: "date",
                nullable: true);

            migrationBuilder.Sql("UPDATE inventory.\"PurchaseInvoices\" SET \"PaymentMethod\" = 'BeforeTreasury' WHERE \"Status\" <> 'Draft';");

            migrationBuilder.CreateTable(
                name: "SafeTransactions",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Fund = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    BusinessDate = table.Column<DateOnly>(type: "date", nullable: false),
                    RevenueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    SupplierId = table.Column<Guid>(type: "uuid", nullable: true),
                    PurchaseInvoiceId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExpenseItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    Notes = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    RecordedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ApprovedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ReversalOf = table.Column<Guid>(type: "uuid", nullable: true),
                    VoidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VoidedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SafeTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SafeTransactions_ExpenseItems_ExpenseItemId",
                        column: x => x.ExpenseItemId,
                        principalSchema: "inventory",
                        principalTable: "ExpenseItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SafeTransactions_PurchaseInvoices_PurchaseInvoiceId",
                        column: x => x.PurchaseInvoiceId,
                        principalSchema: "inventory",
                        principalTable: "PurchaseInvoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SafeTransactions_SafeTransactions_ReversalOf",
                        column: x => x.ReversalOf,
                        principalSchema: "inventory",
                        principalTable: "SafeTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SafeTransactions_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalSchema: "inventory",
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SupplierLedgerEntries",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    BusinessDate = table.Column<DateOnly>(type: "date", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReversalOf = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierLedgerEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplierLedgerEntries_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalSchema: "inventory",
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SafeTransactions_ExpenseItemId",
                schema: "inventory",
                table: "SafeTransactions",
                column: "ExpenseItemId");

            migrationBuilder.CreateIndex(
                name: "IX_SafeTransactions_Fund_BusinessDate",
                schema: "inventory",
                table: "SafeTransactions",
                columns: new[] { "Fund", "BusinessDate" });

            migrationBuilder.CreateIndex(
                name: "IX_SafeTransactions_PurchaseInvoiceId",
                schema: "inventory",
                table: "SafeTransactions",
                column: "PurchaseInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_SafeTransactions_RevenueDate",
                schema: "inventory",
                table: "SafeTransactions",
                column: "RevenueDate");

            migrationBuilder.CreateIndex(
                name: "IX_SafeTransactions_ReversalOf",
                schema: "inventory",
                table: "SafeTransactions",
                column: "ReversalOf",
                unique: true,
                filter: "\"ReversalOf\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SafeTransactions_SupplierId",
                schema: "inventory",
                table: "SafeTransactions",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierLedgerEntries_SourceId_ReversalOf",
                schema: "inventory",
                table: "SupplierLedgerEntries",
                columns: new[] { "SourceId", "ReversalOf" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierLedgerEntries_SupplierId_BusinessDate",
                schema: "inventory",
                table: "SupplierLedgerEntries",
                columns: new[] { "SupplierId", "BusinessDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SafeTransactions",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "SupplierLedgerEntries",
                schema: "inventory");

            migrationBuilder.DropColumn(
                name: "PaymentMethod",
                schema: "inventory",
                table: "PurchaseInvoices");

            migrationBuilder.DropColumn(
                name: "RevenueDate",
                schema: "inventory",
                table: "PurchaseInvoices");
        }
    }
}
