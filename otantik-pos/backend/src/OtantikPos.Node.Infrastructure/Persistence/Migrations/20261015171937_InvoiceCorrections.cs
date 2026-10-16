using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OtantikPos.Node.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InvoiceCorrections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ReplacedBy",
                schema: "inventory",
                table: "PurchaseInvoices",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RevisionOf",
                schema: "inventory",
                table: "PurchaseInvoices",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReplacedBy",
                schema: "inventory",
                table: "PurchaseInvoices");

            migrationBuilder.DropColumn(
                name: "RevisionOf",
                schema: "inventory",
                table: "PurchaseInvoices");
        }
    }
}
