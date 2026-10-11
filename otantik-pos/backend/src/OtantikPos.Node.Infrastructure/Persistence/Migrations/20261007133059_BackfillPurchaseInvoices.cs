using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OtantikPos.Node.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BackfillPurchaseInvoices : Migration
    {
        /// <inheritdoc />
        // Deliveries taken in before invoices were kept: each filed as the posted invoice it was, so
        // the history starts with the first delivery. No supplier or paper was recorded; the lines
        // are in the material's own unit, at what each was booked at.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO inventory."PurchaseInvoices" ("Id", "Status", "CreatedAtUtc", "CreatedBy", "PostedAtUtc", "PostedBy")
                SELECT m."SourceId", 'Posted', min(m."OccurredAtUtc"), coalesce(max(m."RecordedBy"), ''),
                       min(m."OccurredAtUtc"), max(m."RecordedBy")
                FROM inventory."StockMovements" m
                WHERE m."Reason" = 'Purchase'
                  AND NOT EXISTS (SELECT 1 FROM inventory."PurchaseInvoices" i WHERE i."Id" = m."SourceId")
                GROUP BY m."SourceId";

                INSERT INTO inventory."PurchaseInvoiceLines"
                    ("Id", "InvoiceId", "RawMaterialId", "EnteredQuantity", "UnitLabel", "UnitSize", "Quantity", "Cost")
                SELECT gen_random_uuid(), m."SourceId", m."RawMaterialId", m."Quantity",
                       CASE r."Unit" WHEN 'Gram' THEN 'g' WHEN 'Millilitre' THEN 'ml' ELSE 'pc' END,
                       1, m."Quantity", round(m."Quantity" * m."UnitCost", 2)
                FROM inventory."StockMovements" m
                JOIN inventory."RawMaterials" r ON r."Id" = m."RawMaterialId"
                WHERE m."Reason" = 'Purchase'
                  AND NOT EXISTS (SELECT 1 FROM inventory."PurchaseInvoiceLines" l WHERE l."InvoiceId" = m."SourceId");
                """);
        }

        /// <inheritdoc />
        // The filed deliveries stay: they are the record of what came in.
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
