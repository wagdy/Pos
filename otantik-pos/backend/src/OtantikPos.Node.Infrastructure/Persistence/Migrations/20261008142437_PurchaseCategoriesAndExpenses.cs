using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OtantikPos.Node.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PurchaseCategoriesAndExpenses : Migration
    {
        // The categories typed on materials become purchase categories: one per name, capitals and
        // extra spaces ignored, as PurchaseCategory.Normalize compares them, spelled as most of its
        // materials had it (a capital first, on a tie). General is made with its fixed id, for every
        // material that had none. Lines already on invoices are stock lines.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PurchaseCategories",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NameAr = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ExpenseLine = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseCategories", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseCategories_NormalizedName",
                schema: "inventory",
                table: "PurchaseCategories",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CategoryId",
                schema: "inventory",
                table: "RawMaterials",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql("""
                INSERT INTO inventory."PurchaseCategories" ("Id", "Name", "NormalizedName", "NameAr", "ExpenseLine", "CreatedAtUtc")
                VALUES ('3f3f4d50-4158-44c8-bb35-e3dd347717ea', 'General', 'GENERAL', 'عام', 'Controllable', now());

                INSERT INTO inventory."PurchaseCategories" ("Id", "Name", "NormalizedName", "ExpenseLine", "CreatedAtUtc")
                SELECT gen_random_uuid(), picked."Name", picked."NormalizedName", 'Controllable', now()
                FROM (
                    SELECT DISTINCT ON (n."NormalizedName") n."Name", n."NormalizedName"
                    FROM (
                        SELECT regexp_replace(trim("Category"), '\s+', ' ', 'g') AS "Name",
                               upper(regexp_replace(trim("Category"), '\s+', ' ', 'g')) AS "NormalizedName"
                        FROM inventory."RawMaterials"
                        WHERE trim(coalesce("Category", '')) <> ''
                    ) n
                    WHERE n."NormalizedName" <> 'GENERAL'
                    GROUP BY n."NormalizedName", n."Name"
                    ORDER BY n."NormalizedName", count(*) DESC, (left(n."Name", 1) = upper(left(n."Name", 1))) DESC, n."Name"
                ) picked;

                UPDATE inventory."RawMaterials" m SET "CategoryId" = c."Id"
                FROM inventory."PurchaseCategories" c
                WHERE c."NormalizedName" = upper(regexp_replace(trim(m."Category"), '\s+', ' ', 'g'));

                UPDATE inventory."RawMaterials" SET "CategoryId" = '3f3f4d50-4158-44c8-bb35-e3dd347717ea'
                WHERE "CategoryId" IS NULL;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "CategoryId",
                schema: "inventory",
                table: "RawMaterials",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.DropColumn(
                name: "Category",
                schema: "inventory",
                table: "RawMaterials");

            migrationBuilder.CreateIndex(
                name: "IX_RawMaterials_CategoryId",
                schema: "inventory",
                table: "RawMaterials",
                column: "CategoryId");

            migrationBuilder.AddForeignKey(
                name: "FK_RawMaterials_PurchaseCategories_CategoryId",
                schema: "inventory",
                table: "RawMaterials",
                column: "CategoryId",
                principalSchema: "inventory",
                principalTable: "PurchaseCategories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.CreateTable(
                name: "ExpenseItems",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NameAr = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Unit = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ArchivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExpenseItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExpenseItems_PurchaseCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalSchema: "inventory",
                        principalTable: "PurchaseCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseItems_CategoryId",
                schema: "inventory",
                table: "ExpenseItems",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseItems_NormalizedName",
                schema: "inventory",
                table: "ExpenseItems",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.AddColumn<string>(
                name: "ItemType",
                schema: "inventory",
                table: "PurchaseInvoiceLines",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Inventory");

            migrationBuilder.AlterColumn<Guid>(
                name: "RawMaterialId",
                schema: "inventory",
                table: "PurchaseInvoiceLines",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "ExpenseItemId",
                schema: "inventory",
                table: "PurchaseInvoiceLines",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseInvoiceLines_ExpenseItemId",
                schema: "inventory",
                table: "PurchaseInvoiceLines",
                column: "ExpenseItemId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseInvoiceLines_InvoiceId_ExpenseItemId",
                schema: "inventory",
                table: "PurchaseInvoiceLines",
                columns: new[] { "InvoiceId", "ExpenseItemId" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_PurchaseInvoiceLines_Item",
                schema: "inventory",
                table: "PurchaseInvoiceLines",
                sql: "(\"ItemType\" = 'Inventory' AND \"RawMaterialId\" IS NOT NULL AND \"ExpenseItemId\" IS NULL) OR (\"ItemType\" = 'Expense' AND \"ExpenseItemId\" IS NOT NULL AND \"RawMaterialId\" IS NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseInvoiceLines_ExpenseItems_ExpenseItemId",
                schema: "inventory",
                table: "PurchaseInvoiceLines",
                column: "ExpenseItemId",
                principalSchema: "inventory",
                principalTable: "ExpenseItems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        // Back to the category typed on each material, by its category's name (none for General).
        // Expense lines cannot be told in the old shape and are dropped with the expense items.
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Category",
                schema: "inventory",
                table: "RawMaterials",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE inventory."RawMaterials" m SET "Category" = c."Name"
                FROM inventory."PurchaseCategories" c
                WHERE c."Id" = m."CategoryId" AND c."Id" <> '3f3f4d50-4158-44c8-bb35-e3dd347717ea';

                DELETE FROM inventory."PurchaseInvoiceLines" WHERE "ItemType" = 'Expense';
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseInvoiceLines_ExpenseItems_ExpenseItemId",
                schema: "inventory",
                table: "PurchaseInvoiceLines");

            migrationBuilder.DropForeignKey(
                name: "FK_RawMaterials_PurchaseCategories_CategoryId",
                schema: "inventory",
                table: "RawMaterials");

            migrationBuilder.DropTable(
                name: "ExpenseItems",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "PurchaseCategories",
                schema: "inventory");

            migrationBuilder.DropIndex(
                name: "IX_RawMaterials_CategoryId",
                schema: "inventory",
                table: "RawMaterials");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseInvoiceLines_ExpenseItemId",
                schema: "inventory",
                table: "PurchaseInvoiceLines");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseInvoiceLines_InvoiceId_ExpenseItemId",
                schema: "inventory",
                table: "PurchaseInvoiceLines");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PurchaseInvoiceLines_Item",
                schema: "inventory",
                table: "PurchaseInvoiceLines");

            migrationBuilder.DropColumn(
                name: "CategoryId",
                schema: "inventory",
                table: "RawMaterials");

            migrationBuilder.DropColumn(
                name: "ExpenseItemId",
                schema: "inventory",
                table: "PurchaseInvoiceLines");

            migrationBuilder.DropColumn(
                name: "ItemType",
                schema: "inventory",
                table: "PurchaseInvoiceLines");

            migrationBuilder.AlterColumn<Guid>(
                name: "RawMaterialId",
                schema: "inventory",
                table: "PurchaseInvoiceLines",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
