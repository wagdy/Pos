using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OtantikPos.Node.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CategoryKinds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                schema: "inventory",
                table: "PurchaseCategories",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "RawMaterials");

            // Each category the kind most of what it holds in use is; raw materials on a tie, or
            // when it holds nothing in use, unless what it held was expenses. General stays raw
            // materials: its expense items go to General expenses, below.
            migrationBuilder.Sql("""
                UPDATE inventory."PurchaseCategories" c SET "Kind" = 'Expenses'
                WHERE c."Id" <> '3f3f4d50-4158-44c8-bb35-e3dd347717ea' AND (
                    (SELECT count(*) FROM inventory."ExpenseItems" e WHERE e."CategoryId" = c."Id" AND NOT e."IsArchived")
                        > (SELECT count(*) FROM inventory."RawMaterials" m WHERE m."CategoryId" = c."Id" AND m."ArchivedAtUtc" IS NULL)
                    OR (
                        NOT EXISTS (SELECT 1 FROM inventory."ExpenseItems" e WHERE e."CategoryId" = c."Id" AND NOT e."IsArchived")
                        AND NOT EXISTS (SELECT 1 FROM inventory."RawMaterials" m WHERE m."CategoryId" = c."Id" AND m."ArchivedAtUtc" IS NULL)
                        AND (SELECT count(*) FROM inventory."ExpenseItems" e WHERE e."CategoryId" = c."Id")
                            > (SELECT count(*) FROM inventory."RawMaterials" m WHERE m."CategoryId" = c."Id")));
                """);

            // General expenses, the expenses' General: General's expense items move there.
            migrationBuilder.Sql("""
                INSERT INTO inventory."PurchaseCategories" ("Id", "Name", "NormalizedName", "NameAr", "ExpenseLine", "CreatedAtUtc", "Kind")
                VALUES ('8c2d3b6e-1f4a-4c55-9e1d-6a7b0f2e9c41', 'General expenses', 'GENERAL EXPENSES', 'مصروفات عامة', 'Controllable', now(), 'Expenses')
                ON CONFLICT DO NOTHING;
                UPDATE inventory."ExpenseItems" SET "CategoryId" = '8c2d3b6e-1f4a-4c55-9e1d-6a7b0f2e9c41'
                WHERE "CategoryId" = '3f3f4d50-4158-44c8-bb35-e3dd347717ea'
                    AND EXISTS (SELECT 1 FROM inventory."PurchaseCategories" WHERE "Id" = '8c2d3b6e-1f4a-4c55-9e1d-6a7b0f2e9c41');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE inventory."ExpenseItems" SET "CategoryId" = '3f3f4d50-4158-44c8-bb35-e3dd347717ea'
                WHERE "CategoryId" = '8c2d3b6e-1f4a-4c55-9e1d-6a7b0f2e9c41'
                    AND EXISTS (SELECT 1 FROM inventory."PurchaseCategories" WHERE "Id" = '3f3f4d50-4158-44c8-bb35-e3dd347717ea');
                DELETE FROM inventory."PurchaseCategories" c WHERE c."Id" = '8c2d3b6e-1f4a-4c55-9e1d-6a7b0f2e9c41'
                    AND NOT EXISTS (SELECT 1 FROM inventory."ExpenseItems" e WHERE e."CategoryId" = c."Id")
                    AND NOT EXISTS (SELECT 1 FROM inventory."RawMaterials" m WHERE m."CategoryId" = c."Id");
                """);

            migrationBuilder.DropColumn(
                name: "Kind",
                schema: "inventory",
                table: "PurchaseCategories");
        }
    }
}
