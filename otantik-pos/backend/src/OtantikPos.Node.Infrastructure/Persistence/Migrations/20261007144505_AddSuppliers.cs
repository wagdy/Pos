using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OtantikPos.Node.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSuppliers : Migration
    {
        // The suppliers typed on invoices become suppliers: one per name, capitals and extra
        // spaces ignored, as Supplier.Normalize compares them. Each invoice then points at its own.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Suppliers",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ContactPerson = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ArchivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Suppliers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_NormalizedName",
                schema: "inventory",
                table: "Suppliers",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SupplierId",
                schema: "inventory",
                table: "PurchaseInvoices",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql("""
                INSERT INTO inventory."Suppliers" ("Id", "Name", "NormalizedName", "IsArchived", "CreatedAtUtc")
                SELECT gen_random_uuid(), (array_agg(n."Name" ORDER BY n."CreatedAtUtc" DESC))[1], n."NormalizedName", false, min(n."CreatedAtUtc")
                FROM (
                    SELECT regexp_replace(trim("SupplierName"), '\s+', ' ', 'g') AS "Name",
                           upper(regexp_replace(trim("SupplierName"), '\s+', ' ', 'g')) AS "NormalizedName",
                           "CreatedAtUtc"
                    FROM inventory."PurchaseInvoices"
                    WHERE trim(coalesce("SupplierName", '')) <> ''
                ) n
                GROUP BY n."NormalizedName";

                UPDATE inventory."PurchaseInvoices" i SET "SupplierId" = s."Id"
                FROM inventory."Suppliers" s
                WHERE s."NormalizedName" = upper(regexp_replace(trim(i."SupplierName"), '\s+', ' ', 'g'));
                """);

            migrationBuilder.DropIndex(
                name: "IX_PurchaseInvoices_SupplierName",
                schema: "inventory",
                table: "PurchaseInvoices");

            migrationBuilder.DropColumn(
                name: "SupplierName",
                schema: "inventory",
                table: "PurchaseInvoices");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseInvoices_SupplierId",
                schema: "inventory",
                table: "PurchaseInvoices",
                column: "SupplierId");

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseInvoices_Suppliers_SupplierId",
                schema: "inventory",
                table: "PurchaseInvoices",
                column: "SupplierId",
                principalSchema: "inventory",
                principalTable: "Suppliers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        // Back to the name written on each invoice.
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SupplierName",
                schema: "inventory",
                table: "PurchaseInvoices",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE inventory."PurchaseInvoices" i SET "SupplierName" = s."Name"
                FROM inventory."Suppliers" s
                WHERE s."Id" = i."SupplierId";
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseInvoices_Suppliers_SupplierId",
                schema: "inventory",
                table: "PurchaseInvoices");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseInvoices_SupplierId",
                schema: "inventory",
                table: "PurchaseInvoices");

            migrationBuilder.DropColumn(
                name: "SupplierId",
                schema: "inventory",
                table: "PurchaseInvoices");

            migrationBuilder.DropTable(
                name: "Suppliers",
                schema: "inventory");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseInvoices_SupplierName",
                schema: "inventory",
                table: "PurchaseInvoices",
                column: "SupplierName");
        }
    }
}
