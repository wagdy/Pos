using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OtantikPos.Node.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TakingsGross : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "GrossAmount",
                schema: "inventory",
                table: "SafeTransactions",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            // Takings entered before were what was left of the day's cash once the invoices paid
            // straight out of it were taken off: their gross is that and what the drawer paid, on
            // each day's latest standing takings, so the safe and the reports stay as they were. A
            // void's is the opposite of its entry's. Takings with the tax in them have it in the gross.
            migrationBuilder.Sql("""
                UPDATE inventory."SafeTransactions" SET "GrossAmount" = "Amount" WHERE "Kind" = 'RevenueTransfer';

                UPDATE inventory."SafeTransactions" t
                SET "GrossAmount" = t."Amount" + d.paid
                FROM (SELECT "RevenueDate", -SUM("Amount") AS paid
                      FROM inventory."SafeTransactions" WHERE "Fund" = 'DayRevenue' GROUP BY "RevenueDate") d
                WHERE t."Kind" = 'RevenueTransfer' AND t."RevenueDate" = d."RevenueDate" AND d.paid <> 0
                  AND t."Id" = (SELECT s."Id" FROM inventory."SafeTransactions" s
                                WHERE s."Kind" = 'RevenueTransfer' AND s."RevenueDate" = t."RevenueDate"
                                  AND s."ReversalOf" IS NULL AND s."VoidedAtUtc" IS NULL
                                ORDER BY s."RecordedAtUtc" DESC LIMIT 1);

                UPDATE inventory."SafeTransactions"
                SET "TaxAmount" = "GrossAmount" - ROUND("GrossAmount" / (1 + "TaxPercent" / 100), 2)
                WHERE "Kind" = 'RevenueTransfer' AND "IncludesTax" AND "TaxPercent" IS NOT NULL AND "GrossAmount" <> "Amount";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GrossAmount",
                schema: "inventory",
                table: "SafeTransactions");
        }
    }
}
