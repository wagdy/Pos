using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OtantikPos.Node.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PrepRecipeCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RecipeCategoryId",
                schema: "inventory",
                table: "PrepRecipes",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrepRecipes_RecipeCategoryId",
                schema: "inventory",
                table: "PrepRecipes",
                column: "RecipeCategoryId");

            migrationBuilder.AddForeignKey(
                name: "FK_PrepRecipes_RecipeCategories_RecipeCategoryId",
                schema: "inventory",
                table: "PrepRecipes",
                column: "RecipeCategoryId",
                principalSchema: "inventory",
                principalTable: "RecipeCategories",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PrepRecipes_RecipeCategories_RecipeCategoryId",
                schema: "inventory",
                table: "PrepRecipes");

            migrationBuilder.DropIndex(
                name: "IX_PrepRecipes_RecipeCategoryId",
                schema: "inventory",
                table: "PrepRecipes");

            migrationBuilder.DropColumn(
                name: "RecipeCategoryId",
                schema: "inventory",
                table: "PrepRecipes");
        }
    }
}
