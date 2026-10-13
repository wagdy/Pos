using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OtantikPos.Node.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The deletion requests become approval requests: the same table, renamed and kept with its
    /// history, which now also holds new and changed prep items and production orders (Action,
    /// Payload) and what each changes (Changes). One request waits per record, whatever it asks.
    /// Production orders can be voided, and corrected by a batch that names the one it replaced.
    /// </summary>
    public partial class ApprovalRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DeletionRequests_Module_RecordId",
                schema: "inventory",
                table: "DeletionRequests");

            migrationBuilder.RenameTable(
                name: "DeletionRequests",
                schema: "inventory",
                newName: "ApprovalRequests",
                newSchema: "inventory");

            migrationBuilder.Sql("ALTER TABLE inventory.\"ApprovalRequests\" RENAME CONSTRAINT \"PK_DeletionRequests\" TO \"PK_ApprovalRequests\";");

            migrationBuilder.RenameIndex(
                name: "IX_DeletionRequests_Status_RequestedAtUtc",
                schema: "inventory",
                table: "ApprovalRequests",
                newName: "IX_ApprovalRequests_Status_RequestedAtUtc");

            // Every request until now was a deletion.
            migrationBuilder.AddColumn<string>(
                name: "Action",
                schema: "inventory",
                table: "ApprovalRequests",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Delete");

            migrationBuilder.AddColumn<string>(
                name: "Payload",
                schema: "inventory",
                table: "ApprovalRequests",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Changes",
                schema: "inventory",
                table: "ApprovalRequests",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_RecordId",
                schema: "inventory",
                table: "ApprovalRequests",
                column: "RecordId",
                unique: true,
                filter: "\"Status\" = 'Pending'");

            migrationBuilder.AddColumn<string>(
                name: "ApprovedBy",
                schema: "inventory",
                table: "ProductionOrders",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReplacedBy",
                schema: "inventory",
                table: "ProductionOrders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RevisionOf",
                schema: "inventory",
                table: "ProductionOrders",
                type: "uuid",
                nullable: true);

            // Every batch until now was made and stands.
            migrationBuilder.AddColumn<string>(
                name: "Status",
                schema: "inventory",
                table: "ProductionOrders",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Made");

            migrationBuilder.AddColumn<DateTime>(
                name: "VoidedAtUtc",
                schema: "inventory",
                table: "ProductionOrders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VoidedBy",
                schema: "inventory",
                table: "ProductionOrders",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "ApprovedBy", schema: "inventory", table: "ProductionOrders");
            migrationBuilder.DropColumn(name: "ReplacedBy", schema: "inventory", table: "ProductionOrders");
            migrationBuilder.DropColumn(name: "RevisionOf", schema: "inventory", table: "ProductionOrders");
            migrationBuilder.DropColumn(name: "Status", schema: "inventory", table: "ProductionOrders");
            migrationBuilder.DropColumn(name: "VoidedAtUtc", schema: "inventory", table: "ProductionOrders");
            migrationBuilder.DropColumn(name: "VoidedBy", schema: "inventory", table: "ProductionOrders");

            migrationBuilder.DropIndex(
                name: "IX_ApprovalRequests_RecordId",
                schema: "inventory",
                table: "ApprovalRequests");

            // Only deletions fit the old table.
            migrationBuilder.Sql("DELETE FROM inventory.\"ApprovalRequests\" WHERE \"Action\" <> 'Delete';");
            migrationBuilder.DropColumn(name: "Action", schema: "inventory", table: "ApprovalRequests");
            migrationBuilder.DropColumn(name: "Payload", schema: "inventory", table: "ApprovalRequests");
            migrationBuilder.DropColumn(name: "Changes", schema: "inventory", table: "ApprovalRequests");

            migrationBuilder.RenameIndex(
                name: "IX_ApprovalRequests_Status_RequestedAtUtc",
                schema: "inventory",
                table: "ApprovalRequests",
                newName: "IX_DeletionRequests_Status_RequestedAtUtc");

            migrationBuilder.Sql("ALTER TABLE inventory.\"ApprovalRequests\" RENAME CONSTRAINT \"PK_ApprovalRequests\" TO \"PK_DeletionRequests\";");

            migrationBuilder.RenameTable(
                name: "ApprovalRequests",
                schema: "inventory",
                newName: "DeletionRequests",
                newSchema: "inventory");

            migrationBuilder.CreateIndex(
                name: "IX_DeletionRequests_Module_RecordId",
                schema: "inventory",
                table: "DeletionRequests",
                columns: new[] { "Module", "RecordId" },
                unique: true,
                filter: "\"Status\" = 'Pending'");
        }
    }
}
