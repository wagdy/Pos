using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OtantikPos.Node.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPurchaseInvoices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PurchaseInvoices",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SupplierName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SupplierInvoiceNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    InvoiceDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PostedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PostedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AttachmentFileName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AttachmentContentType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AttachmentBytes = table.Column<int>(type: "integer", nullable: true),
                    AttachedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AttachedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseInvoices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseInvoiceAttachments",
                schema: "inventory",
                columns: table => new
                {
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Content = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseInvoiceAttachments", x => x.InvoiceId);
                    table.ForeignKey(
                        name: "FK_PurchaseInvoiceAttachments_PurchaseInvoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalSchema: "inventory",
                        principalTable: "PurchaseInvoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseInvoiceLines",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    RawMaterialId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnteredQuantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    UnitLabel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    UnitSize = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    Cost = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseInvoiceLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PurchaseInvoiceLines_PurchaseInvoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalSchema: "inventory",
                        principalTable: "PurchaseInvoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PurchaseInvoiceLines_RawMaterials_RawMaterialId",
                        column: x => x.RawMaterialId,
                        principalSchema: "inventory",
                        principalTable: "RawMaterials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseInvoiceLines_InvoiceId_RawMaterialId",
                schema: "inventory",
                table: "PurchaseInvoiceLines",
                columns: new[] { "InvoiceId", "RawMaterialId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseInvoiceLines_RawMaterialId",
                schema: "inventory",
                table: "PurchaseInvoiceLines",
                column: "RawMaterialId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseInvoices_Status_PostedAtUtc",
                schema: "inventory",
                table: "PurchaseInvoices",
                columns: new[] { "Status", "PostedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseInvoices_SupplierName",
                schema: "inventory",
                table: "PurchaseInvoices",
                column: "SupplierName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PurchaseInvoiceAttachments",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "PurchaseInvoiceLines",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "PurchaseInvoices",
                schema: "inventory");
        }
    }
}
