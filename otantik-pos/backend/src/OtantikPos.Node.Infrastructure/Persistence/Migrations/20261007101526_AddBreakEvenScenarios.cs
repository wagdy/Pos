using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OtantikPos.Node.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBreakEvenScenarios : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<decimal>>(
                name: "BreakEvenScenarios",
                schema: "inventory",
                table: "CostingSettings",
                type: "numeric[]",
                nullable: false,
                defaultValueSql: "'{}'::numeric[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BreakEvenScenarios",
                schema: "inventory",
                table: "CostingSettings");
        }
    }
}
