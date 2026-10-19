using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OtantikPos.Inventory.Domain.Approvals;
using OtantikPos.Inventory.Domain.Categories;
using OtantikPos.Inventory.Domain.Expenses;
using OtantikPos.Inventory.Domain.Production;
using OtantikPos.Inventory.Domain.Purchases;
using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Inventory.Domain.Recipes;
using OtantikPos.Inventory.Domain.StockCounts;
using OtantikPos.Inventory.Domain.StockMovements;
using OtantikPos.Inventory.Domain.Suppliers;
using OtantikPos.Inventory.Domain.Treasury;

namespace OtantikPos.Node.Infrastructure.Persistence.Configurations;

internal sealed class RawMaterialConfiguration : IEntityTypeConfiguration<RawMaterial>
{
    public void Configure(EntityTypeBuilder<RawMaterial> builder)
    {
        builder.ToTable("RawMaterials", NodeDbContext.InventorySchema);
        builder.Property(m => m.Name).HasMaxLength(200);
        builder.Property(m => m.Unit).IsEnumName();
        builder.Property(m => m.Kind).IsEnumName();
        builder.Ignore(m => m.IsPrepItem);
        builder.Property(m => m.QuantityOnHand).IsQuantity();
        builder.Property(m => m.ReorderLevel).IsQuantity();
        builder.Property(m => m.Code).HasMaxLength(32);
        // A category is deleted only once what is in it has moved.
        builder.HasOne<PurchaseCategory>().WithMany().HasForeignKey(m => m.CategoryId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(m => m.PurchaseUnit).HasMaxLength(32);
        builder.Property(m => m.PurchaseUnitSize).IsQuantity();
        builder.Property(m => m.AverageCost).IsUnitCost();
        builder.Property(m => m.DefaultYieldPercent).HasPrecision(5, 2).HasDefaultValue(100m);
        builder.Ignore(m => m.CostPerPurchaseUnit);
        builder.Ignore(m => m.IsArchived);
        builder.Property(m => m.ArchivedBy).HasMaxLength(200);
    }
}

internal sealed class RecipeConfiguration : IEntityTypeConfiguration<Recipe>
{
    public void Configure(EntityTypeBuilder<Recipe> builder)
    {
        builder.ToTable("Recipes", NodeDbContext.InventorySchema);
        builder.Ignore(r => r.Target);
        builder.Ignore(r => r.IsLinked);
        builder.Ignore(r => r.IsDeleted);
        builder.Property(r => r.Name).HasMaxLength(200);
        // A category deleted with recipes still in it has moved them first; any deleted recipe
        // left behind is uncategorized rather than holding the delete up.
        builder.HasOne<RecipeCategory>().WithMany().HasForeignKey(r => r.RecipeCategoryId).OnDelete(DeleteBehavior.SetNull);
        builder.Property(r => r.PlannedPrice).HasPrecision(18, 2);
        builder.Property(r => r.TargetKind).HasConversion<string>().HasMaxLength(32);
        builder.Property(r => r.DeletedBy).HasMaxLength(200);
        builder.Property(r => r.Portions).HasDefaultValue(1);

        // One active recipe per target; deleted and off-menu ones are left out. NULLS NOT
        // DISTINCT because VariantId is null for a base recipe and an add-on, and PostgreSQL
        // otherwise treats every null as different from every other, allowing a second base
        // recipe for the same item. Needs PostgreSQL 15 or newer.
        builder.HasIndex(r => new { r.TargetKind, r.CatalogItemId, r.VariantId })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasFilter("\"DeletedAtUtc\" IS NULL AND \"CatalogItemId\" IS NOT NULL");

        builder.HasMany(r => r.Ingredients)
            .WithOne()
            .HasForeignKey(i => i.RecipeId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(r => r.Ingredients)
            .HasField("_ingredients")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class RecipeCategoryConfiguration : IEntityTypeConfiguration<RecipeCategory>
{
    public void Configure(EntityTypeBuilder<RecipeCategory> builder)
    {
        builder.ToTable("RecipeCategories", NodeDbContext.InventorySchema);
        builder.Property(c => c.Name).HasMaxLength(100);
        builder.Property(c => c.NormalizedName).HasMaxLength(100);
        builder.Property(c => c.NameAr).HasMaxLength(100);
        builder.HasIndex(c => c.NormalizedName).IsUnique();
    }
}

internal sealed class RecipeIngredientConfiguration : IEntityTypeConfiguration<RecipeIngredient>
{
    public void Configure(EntityTypeBuilder<RecipeIngredient> builder)
    {
        builder.ToTable("RecipeIngredients", NodeDbContext.InventorySchema);
        builder.Property(i => i.Quantity).IsQuantity();
        builder.Property(i => i.YieldPercent).HasPrecision(5, 2).HasDefaultValue(100m);
        builder.Ignore(i => i.AsPurchasedQuantity);

        builder.HasOne<RawMaterial>()
            .WithMany()
            .HasForeignKey(i => i.RawMaterialId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(i => new { i.RecipeId, i.RawMaterialId }).IsUnique();
    }
}

internal sealed class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> builder)
    {
        builder.ToTable("StockMovements", NodeDbContext.InventorySchema);
        builder.Property(m => m.Quantity).IsQuantity();
        builder.Property(m => m.Reason).IsEnumName();
        builder.Property(m => m.UnitCost).IsUnitCost();
        builder.Property(m => m.Note).HasMaxLength(200);
        builder.Property(m => m.RecordedBy).HasMaxLength(200);
        builder.Ignore(m => m.IsReversal);

        builder.HasOne<RawMaterial>()
            .WithMany()
            .HasForeignKey(m => m.RawMaterialId)
            .OnDelete(DeleteBehavior.Restrict);

        // The idempotency guarantee behind IStockMovementRepository.ExistsAsync: a redelivered
        // event that slipped past that check fails here instead of posting twice. SourceId is
        // the order item's PublicId. ReversalOf lets one reversing entry sit beside each; NULLS
        // NOT DISTINCT, or every original (no ReversalOf) would count as different from the next.
        builder.HasIndex(m => new { m.SourceId, m.Reason, m.RawMaterialId, m.ReversalOf }).IsUnique().AreNullsDistinct(false);
        builder.HasIndex(m => new { m.RawMaterialId, m.OccurredAtUtc });
    }
}

internal sealed class StockCountConfiguration : IEntityTypeConfiguration<StockCount>
{
    public void Configure(EntityTypeBuilder<StockCount> builder)
    {
        builder.ToTable("StockCounts", NodeDbContext.InventorySchema);
        builder.Property(c => c.Status).IsEnumName();
        builder.Property(c => c.StartedBy).HasMaxLength(200);
        builder.Property(c => c.PostedBy).HasMaxLength(200);
        builder.Property(c => c.VoidedBy).HasMaxLength(200);
        builder.HasMany(c => c.Lines).WithOne().HasForeignKey(l => l.StockCountId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(c => c.Lines).HasField("_lines").UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasIndex(c => c.Status);
    }
}

internal sealed class StockCountLineConfiguration : IEntityTypeConfiguration<StockCountLine>
{
    public void Configure(EntityTypeBuilder<StockCountLine> builder)
    {
        builder.ToTable("StockCountLines", NodeDbContext.InventorySchema);
        builder.Property(l => l.CountedQuantity).IsQuantity();
        builder.Property(l => l.BookQuantity).IsQuantity();
        builder.Property(l => l.UnitCost).IsUnitCost();
        builder.HasOne<RawMaterial>().WithMany().HasForeignKey(l => l.RawMaterialId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(l => new { l.StockCountId, l.RawMaterialId }).IsUnique();
    }
}

internal sealed class PurchaseInvoiceConfiguration : IEntityTypeConfiguration<PurchaseInvoice>
{
    public void Configure(EntityTypeBuilder<PurchaseInvoice> builder)
    {
        builder.ToTable("PurchaseInvoices", NodeDbContext.InventorySchema);
        builder.Property(i => i.Status).IsEnumName();
        builder.Property(i => i.SupplierInvoiceNumber).HasMaxLength(64);
        builder.Property(i => i.CreatedBy).HasMaxLength(200);
        builder.Property(i => i.PostedBy).HasMaxLength(200);
        builder.Property(i => i.VoidedBy).HasMaxLength(200);
        builder.Property(i => i.AttachmentFileName).HasMaxLength(200);
        builder.Property(i => i.AttachmentContentType).HasMaxLength(64);
        builder.Property(i => i.AttachedBy).HasMaxLength(200);
        builder.Property(i => i.PaymentMethod).IsEnumName();
        builder.Ignore(i => i.HasAttachment);
        builder.Ignore(i => i.Total);
        builder.HasMany(i => i.Lines).WithOne().HasForeignKey(l => l.InvoiceId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(i => i.Lines).HasField("_lines").UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasIndex(i => new { i.Status, i.PostedAtUtc });
        builder.HasOne<Supplier>().WithMany().HasForeignKey(i => i.SupplierId).OnDelete(DeleteBehavior.Restrict);
    }
}

// The safe's ledger: one row per cash movement, in or out, reversals beside what they undo.
internal sealed class SafeTransactionConfiguration : IEntityTypeConfiguration<SafeTransaction>
{
    public void Configure(EntityTypeBuilder<SafeTransaction> builder)
    {
        builder.ToTable("SafeTransactions", NodeDbContext.InventorySchema);
        builder.Property(t => t.Kind).IsEnumName();
        builder.Property(t => t.Fund).IsEnumName();
        builder.Property(t => t.Source).IsEnumName();
        builder.Property(t => t.Amount).HasPrecision(18, 2);
        builder.Property(t => t.TaxPercent).HasPrecision(5, 2);
        builder.Property(t => t.TaxAmount).HasPrecision(18, 2);
        builder.Property(t => t.GrossAmount).HasPrecision(18, 2);
        builder.Property(t => t.Notes).HasMaxLength(300);
        builder.Property(t => t.RecordedBy).HasMaxLength(200);
        builder.Property(t => t.ApprovedBy).HasMaxLength(200);
        builder.Property(t => t.VoidedBy).HasMaxLength(200);
        builder.Ignore(t => t.NetAmount);
        builder.Ignore(t => t.PaidOut);
        builder.Ignore(t => t.IsReversal);
        builder.Ignore(t => t.IsVoided);
        builder.HasOne<Supplier>().WithMany().HasForeignKey(t => t.SupplierId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PurchaseInvoice>().WithMany().HasForeignKey(t => t.PurchaseInvoiceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ExpenseItem>().WithMany().HasForeignKey(t => t.ExpenseItemId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SafeTransaction>().WithMany().HasForeignKey(t => t.ReversalOf).OnDelete(DeleteBehavior.Restrict);
        // One reversal for each entry.
        builder.HasIndex(t => t.ReversalOf).IsUnique().HasFilter("\"ReversalOf\" IS NOT NULL");
        builder.HasIndex(t => new { t.Fund, t.BusinessDate });
        builder.HasIndex(t => t.RevenueDate);
        builder.HasIndex(t => t.PurchaseInvoiceId);
    }
}

// What is owed to each supplier, line by line.
internal sealed class SupplierLedgerEntryConfiguration : IEntityTypeConfiguration<SupplierLedgerEntry>
{
    public void Configure(EntityTypeBuilder<SupplierLedgerEntry> builder)
    {
        builder.ToTable("SupplierLedgerEntries", NodeDbContext.InventorySchema);
        builder.Property(e => e.Kind).IsEnumName();
        builder.Property(e => e.Amount).HasPrecision(18, 2);
        builder.Property(e => e.RecordedBy).HasMaxLength(200);
        builder.Ignore(e => e.IsReversal);
        builder.HasOne<Supplier>().WithMany().HasForeignKey(e => e.SupplierId).OnDelete(DeleteBehavior.Restrict);
        // A record is on a supplier's account once, and undone once.
        builder.HasIndex(e => new { e.SourceId, e.ReversalOf }).IsUnique().AreNullsDistinct(false);
        builder.HasIndex(e => new { e.SupplierId, e.BusinessDate });
    }
}

internal sealed class SupplierConfiguration : IEntityTypeConfiguration<Supplier>
{
    public void Configure(EntityTypeBuilder<Supplier> builder)
    {
        builder.ToTable("Suppliers", NodeDbContext.InventorySchema);
        builder.Property(s => s.Name).HasMaxLength(200);
        builder.Property(s => s.NormalizedName).HasMaxLength(200);
        builder.Property(s => s.ContactPerson).HasMaxLength(200);
        builder.Property(s => s.Phone).HasMaxLength(32);
        builder.Property(s => s.Email).HasMaxLength(200);
        builder.Property(s => s.Notes).HasMaxLength(500);
        // Archived or not: two suppliers of one name would make the invoices ambiguous.
        builder.HasIndex(s => s.NormalizedName).IsUnique();
    }
}

internal sealed class PurchaseInvoiceLineConfiguration : IEntityTypeConfiguration<PurchaseInvoiceLine>
{
    public void Configure(EntityTypeBuilder<PurchaseInvoiceLine> builder)
    {
        // A line is a material or an expense, as its type says, never both or neither.
        builder.ToTable("PurchaseInvoiceLines", NodeDbContext.InventorySchema, t => t.HasCheckConstraint("CK_PurchaseInvoiceLines_Item",
            "(\"ItemType\" = 'Inventory' AND \"RawMaterialId\" IS NOT NULL AND \"ExpenseItemId\" IS NULL) OR " +
            "(\"ItemType\" = 'Expense' AND \"ExpenseItemId\" IS NOT NULL AND \"RawMaterialId\" IS NULL)"));
        builder.Property(l => l.EnteredQuantity).IsQuantity();
        builder.Property(l => l.UnitLabel).HasMaxLength(32);
        builder.Property(l => l.UnitSize).IsQuantity();
        builder.Property(l => l.Quantity).IsQuantity();
        builder.Property(l => l.Cost).HasPrecision(18, 2);
        builder.Property(l => l.ItemType).IsEnumName();
        builder.HasOne<RawMaterial>().WithMany().HasForeignKey(l => l.RawMaterialId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ExpenseItem>().WithMany().HasForeignKey(l => l.ExpenseItemId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(l => new { l.InvoiceId, l.RawMaterialId }).IsUnique();
        builder.HasIndex(l => new { l.InvoiceId, l.ExpenseItemId }).IsUnique();
    }
}

internal sealed class ApprovalRequestConfiguration : IEntityTypeConfiguration<ApprovalRequest>
{
    public void Configure(EntityTypeBuilder<ApprovalRequest> builder)
    {
        builder.ToTable("ApprovalRequests", NodeDbContext.InventorySchema);
        builder.Property(r => r.Module).IsEnumName();
        builder.Property(r => r.Action).IsEnumName();
        builder.Property(r => r.Status).IsEnumName();
        builder.Property(r => r.Summary).HasMaxLength(500);
        // What the manager reads, line by line, and what a new or changed record holds until
        // approved: both JSON, read and written whole.
        builder.Ignore(r => r.Changes);
        builder.Property<List<ApprovalChange>>("_changes")
            .HasColumnName("Changes")
            .HasColumnType("jsonb")
            .HasConversion(
                changes => JsonSerializer.Serialize(changes, (JsonSerializerOptions?)null),
                json => JsonSerializer.Deserialize<List<ApprovalChange>>(json, (JsonSerializerOptions?)null) ?? new List<ApprovalChange>(),
                new ValueComparer<List<ApprovalChange>>(
                    (a, b) => a!.SequenceEqual(b!),
                    changes => changes.Aggregate(0, (hash, change) => HashCode.Combine(hash, change.GetHashCode())),
                    changes => changes.ToList()));
        builder.Property(r => r.Payload).HasColumnType("jsonb");
        builder.Property(r => r.Reason).HasMaxLength(500);
        builder.Property(r => r.RequestedById).HasMaxLength(200);
        builder.Property(r => r.RequestedBy).HasMaxLength(200);
        builder.Property(r => r.DecidedBy).HasMaxLength(200);
        builder.Property(r => r.DecisionNote).HasMaxLength(500);
        builder.Ignore(r => r.IsPending);
        // At most one request waits for a record, whatever it asks: two would race to change it
        // twice. Record ids are Guids, unique across the modules.
        builder.HasIndex(r => r.RecordId).IsUnique().HasFilter("\"Status\" = 'Pending'");
        builder.HasIndex(r => new { r.Status, r.RequestedAtUtc });
    }
}

internal sealed class PurchaseCategoryConfiguration : IEntityTypeConfiguration<PurchaseCategory>
{
    public void Configure(EntityTypeBuilder<PurchaseCategory> builder)
    {
        builder.ToTable("PurchaseCategories", NodeDbContext.InventorySchema);
        builder.Property(c => c.Name).HasMaxLength(100);
        builder.Property(c => c.NormalizedName).HasMaxLength(100);
        builder.Property(c => c.NameAr).HasMaxLength(100);
        builder.Property(c => c.ExpenseLine).IsEnumName();
        // Raw materials or general expenses; the CategoryKinds migration gave each it had one.
        builder.Property(c => c.Kind).IsEnumName().HasDefaultValue(CategoryKind.RawMaterials).HasSentinel((CategoryKind)0);
        builder.Ignore(c => c.IsGeneral);
        builder.HasIndex(c => c.NormalizedName).IsUnique();
    }
}

internal sealed class ExpenseItemConfiguration : IEntityTypeConfiguration<ExpenseItem>
{
    public void Configure(EntityTypeBuilder<ExpenseItem> builder)
    {
        builder.ToTable("ExpenseItems", NodeDbContext.InventorySchema);
        builder.Property(e => e.Name).HasMaxLength(200);
        builder.Property(e => e.NormalizedName).HasMaxLength(200);
        builder.Property(e => e.NameAr).HasMaxLength(200);
        builder.Property(e => e.Unit).HasMaxLength(32);
        // Archived or not: two of one name would make the invoices ambiguous.
        builder.HasIndex(e => e.NormalizedName).IsUnique();
        builder.HasOne<PurchaseCategory>().WithMany().HasForeignKey(e => e.CategoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PurchaseInvoiceAttachmentConfiguration : IEntityTypeConfiguration<PurchaseInvoiceAttachment>
{
    public void Configure(EntityTypeBuilder<PurchaseInvoiceAttachment> builder)
    {
        builder.ToTable("PurchaseInvoiceAttachments", NodeDbContext.InventorySchema);
        builder.HasKey(a => a.InvoiceId);
        builder.HasOne<PurchaseInvoice>().WithOne().HasForeignKey<PurchaseInvoiceAttachment>(a => a.InvoiceId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PrepRecipeConfiguration : IEntityTypeConfiguration<PrepRecipe>
{
    public void Configure(EntityTypeBuilder<PrepRecipe> builder)
    {
        builder.ToTable("PrepRecipes", NodeDbContext.InventorySchema);
        builder.Ignore(r => r.PrepItemId);
        builder.Property(r => r.BatchQuantity).IsQuantity();
        // Its id is the prep item's: one recipe each, gone with it.
        builder.HasOne<RawMaterial>().WithOne().HasForeignKey<PrepRecipe>(r => r.Id).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(r => r.Ingredients).WithOne().HasForeignKey(i => i.PrepRecipeId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(r => r.Ingredients).HasField("_ingredients").UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasOne<RecipeCategory>().WithMany().HasForeignKey(r => r.RecipeCategoryId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class PrepIngredientConfiguration : IEntityTypeConfiguration<PrepIngredient>
{
    public void Configure(EntityTypeBuilder<PrepIngredient> builder)
    {
        builder.ToTable("PrepRecipeIngredients", NodeDbContext.InventorySchema);
        builder.Property(i => i.Quantity).IsQuantity();
        builder.Property(i => i.YieldPercent).HasPrecision(5, 2);
        builder.Ignore(i => i.AsPurchasedQuantity);
        builder.HasOne<RawMaterial>().WithMany().HasForeignKey(i => i.RawMaterialId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(i => new { i.PrepRecipeId, i.RawMaterialId }).IsUnique();
    }
}

internal sealed class ProductionOrderConfiguration : IEntityTypeConfiguration<ProductionOrder>
{
    public void Configure(EntityTypeBuilder<ProductionOrder> builder)
    {
        builder.ToTable("ProductionOrders", NodeDbContext.InventorySchema);
        builder.Property(o => o.Status).IsEnumName();
        builder.Property(o => o.VoidedBy).HasMaxLength(200);
        builder.Property(o => o.ApprovedBy).HasMaxLength(200);
        builder.Ignore(o => o.IsVoided);
        builder.Property(o => o.PlannedQuantity).IsQuantity();
        builder.Property(o => o.ProducedQuantity).IsQuantity();
        builder.Property(o => o.TotalCost).HasPrecision(18, 2);
        builder.Property(o => o.UnitCost).HasPrecision(18, 6);
        builder.Property(o => o.ProducedBy).HasMaxLength(200);
        builder.Property(o => o.Notes).HasMaxLength(200);
        builder.HasOne<RawMaterial>().WithMany().HasForeignKey(o => o.PrepItemId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(o => o.Lines).WithOne().HasForeignKey(l => l.ProductionOrderId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(o => o.Lines).HasField("_lines").UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasIndex(o => new { o.PrepItemId, o.ProducedAtUtc });
        builder.HasIndex(o => o.ProducedAtUtc);
    }
}

internal sealed class ProductionOrderLineConfiguration : IEntityTypeConfiguration<ProductionOrderLine>
{
    public void Configure(EntityTypeBuilder<ProductionOrderLine> builder)
    {
        builder.ToTable("ProductionOrderLines", NodeDbContext.InventorySchema);
        builder.Property(l => l.Quantity).IsQuantity();
        builder.Property(l => l.UnitCost).HasPrecision(18, 6);
        builder.Property(l => l.Cost).HasPrecision(18, 2);
        builder.HasOne<RawMaterial>().WithMany().HasForeignKey(l => l.RawMaterialId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(l => new { l.ProductionOrderId, l.RawMaterialId }).IsUnique();
    }
}
