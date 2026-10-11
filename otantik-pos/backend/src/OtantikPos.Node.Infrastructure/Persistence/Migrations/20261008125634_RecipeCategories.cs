using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OtantikPos.Node.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecipeCategories : Migration
    {
        // The categories typed on recipes become categories: one per name, capitals and extra
        // spaces ignored, as RecipeCategory.Normalize compares them; their recipes go into them.
        // The rest are left uncategorized, to be sorted when next edited.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RecipeCategories",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NameAr = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeCategories", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecipeCategories_NormalizedName",
                schema: "inventory",
                table: "RecipeCategories",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RecipeCategoryId",
                schema: "inventory",
                table: "Recipes",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql("""
                INSERT INTO inventory."RecipeCategories" ("Id", "Name", "NormalizedName", "CreatedAtUtc")
                SELECT gen_random_uuid(), min(n."Name"), n."NormalizedName", now()
                FROM (
                    SELECT regexp_replace(trim("Category"), '\s+', ' ', 'g') AS "Name",
                           upper(regexp_replace(trim("Category"), '\s+', ' ', 'g')) AS "NormalizedName"
                    FROM inventory."Recipes"
                    WHERE trim(coalesce("Category", '')) <> ''
                ) n
                GROUP BY n."NormalizedName";

                UPDATE inventory."Recipes" r SET "RecipeCategoryId" = c."Id"
                FROM inventory."RecipeCategories" c
                WHERE c."NormalizedName" = upper(regexp_replace(trim(r."Category"), '\s+', ' ', 'g'));
                """);

            migrationBuilder.DropColumn(
                name: "Category",
                schema: "inventory",
                table: "Recipes");

            migrationBuilder.CreateIndex(
                name: "IX_Recipes_RecipeCategoryId",
                schema: "inventory",
                table: "Recipes",
                column: "RecipeCategoryId");

            migrationBuilder.AddForeignKey(
                name: "FK_Recipes_RecipeCategories_RecipeCategoryId",
                schema: "inventory",
                table: "Recipes",
                column: "RecipeCategoryId",
                principalSchema: "inventory",
                principalTable: "RecipeCategories",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        // Back to the category typed on each recipe, by its category's name.
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Category",
                schema: "inventory",
                table: "Recipes",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE inventory."Recipes" r SET "Category" = c."Name"
                FROM inventory."RecipeCategories" c
                WHERE c."Id" = r."RecipeCategoryId";
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_Recipes_RecipeCategories_RecipeCategoryId",
                schema: "inventory",
                table: "Recipes");

            migrationBuilder.DropIndex(
                name: "IX_Recipes_RecipeCategoryId",
                schema: "inventory",
                table: "Recipes");

            migrationBuilder.DropColumn(
                name: "RecipeCategoryId",
                schema: "inventory",
                table: "Recipes");

            migrationBuilder.DropTable(
                name: "RecipeCategories",
                schema: "inventory");
        }
    }
}
