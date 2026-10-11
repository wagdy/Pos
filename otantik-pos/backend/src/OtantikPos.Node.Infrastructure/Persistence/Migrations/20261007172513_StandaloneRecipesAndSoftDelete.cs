using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OtantikPos.Node.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StandaloneRecipesAndSoftDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Recipes_TargetKind_CatalogItemId_VariantId",
                schema: "inventory",
                table: "Recipes");

            migrationBuilder.AlterColumn<string>(
                name: "TargetKind",
                schema: "inventory",
                table: "Recipes",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);

            migrationBuilder.AlterColumn<int>(
                name: "CatalogItemId",
                schema: "inventory",
                table: "Recipes",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<string>(
                name: "Category",
                schema: "inventory",
                table: "Recipes",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAtUtc",
                schema: "inventory",
                table: "Recipes",
                type: "timestamp with time zone",
                nullable: false,
                // The recipes there are: from now, as nothing kept when they were written.
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAtUtc",
                schema: "inventory",
                table: "Recipes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedBy",
                schema: "inventory",
                table: "Recipes",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Name",
                schema: "inventory",
                table: "Recipes",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PlannedPrice",
                schema: "inventory",
                table: "Recipes",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Recipes_TargetKind_CatalogItemId_VariantId",
                schema: "inventory",
                table: "Recipes",
                columns: new[] { "TargetKind", "CatalogItemId", "VariantId" },
                unique: true,
                filter: "\"DeletedAtUtc\" IS NULL AND \"CatalogItemId\" IS NOT NULL")
                .Annotation("Npgsql:NullsDistinct", false);
        }

        // Back to one recipe per menu item and no others: off-menu and deleted recipes cannot be
        // kept there, and go with this step back.
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Recipes_TargetKind_CatalogItemId_VariantId",
                schema: "inventory",
                table: "Recipes");

            migrationBuilder.Sql("""
                DELETE FROM inventory."Recipes" WHERE "CatalogItemId" IS NULL OR "DeletedAtUtc" IS NOT NULL;
                """);

            migrationBuilder.DropColumn(
                name: "Category",
                schema: "inventory",
                table: "Recipes");

            migrationBuilder.DropColumn(
                name: "CreatedAtUtc",
                schema: "inventory",
                table: "Recipes");

            migrationBuilder.DropColumn(
                name: "DeletedAtUtc",
                schema: "inventory",
                table: "Recipes");

            migrationBuilder.DropColumn(
                name: "DeletedBy",
                schema: "inventory",
                table: "Recipes");

            migrationBuilder.DropColumn(
                name: "Name",
                schema: "inventory",
                table: "Recipes");

            migrationBuilder.DropColumn(
                name: "PlannedPrice",
                schema: "inventory",
                table: "Recipes");

            migrationBuilder.AlterColumn<string>(
                name: "TargetKind",
                schema: "inventory",
                table: "Recipes",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "CatalogItemId",
                schema: "inventory",
                table: "Recipes",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Recipes_TargetKind_CatalogItemId_VariantId",
                schema: "inventory",
                table: "Recipes",
                columns: new[] { "TargetKind", "CatalogItemId", "VariantId" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }
    }
}
