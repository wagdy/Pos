using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Inventory.Domain.Recipes;
using OtantikPos.Inventory.Domain.StockCounts;
using OtantikPos.Inventory.Domain.StockMovements;

namespace OtantikPos.Node.Infrastructure.Persistence.Configurations;

internal sealed class RawMaterialConfiguration : IEntityTypeConfiguration<RawMaterial>
{
    public void Configure(EntityTypeBuilder<RawMaterial> builder)
    {
        builder.ToTable("RawMaterials", NodeDbContext.InventorySchema);
        builder.Property(m => m.Name).HasMaxLength(200);
        builder.Property(m => m.Unit).IsEnumName();
        builder.Property(m => m.QuantityOnHand).IsQuantity();
        builder.Property(m => m.ReorderLevel).IsQuantity();
        builder.Property(m => m.Code).HasMaxLength(32);
        builder.Property(m => m.Category).HasMaxLength(100);
        builder.Property(m => m.PurchaseUnit).HasMaxLength(32);
        builder.Property(m => m.PurchaseUnitSize).IsQuantity();
        builder.Property(m => m.AverageCost).IsUnitCost();
        builder.Property(m => m.DefaultYieldPercent).HasPrecision(5, 2).HasDefaultValue(100m);
        builder.Ignore(m => m.CostPerPurchaseUnit);
    }
}

internal sealed class RecipeConfiguration : IEntityTypeConfiguration<Recipe>
{
    public void Configure(EntityTypeBuilder<Recipe> builder)
    {
        builder.ToTable("Recipes", NodeDbContext.InventorySchema);
        builder.Ignore(r => r.Target);
        builder.Property(r => r.TargetKind).IsEnumName();
        builder.Property(r => r.Portions).HasDefaultValue(1);

        // One recipe per target. NULLS NOT DISTINCT because VariantId is null for a base recipe
        // and an add-on, and PostgreSQL otherwise treats every null as different from every
        // other, allowing a second base recipe for the same item. Needs PostgreSQL 15 or newer.
        builder.HasIndex(r => new { r.TargetKind, r.CatalogItemId, r.VariantId })
            .IsUnique()
            .AreNullsDistinct(false);

        builder.HasMany(r => r.Ingredients)
            .WithOne()
            .HasForeignKey(i => i.RecipeId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(r => r.Ingredients)
            .HasField("_ingredients")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
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

        builder.HasOne<RawMaterial>()
            .WithMany()
            .HasForeignKey(m => m.RawMaterialId)
            .OnDelete(DeleteBehavior.Restrict);

        // The idempotency guarantee behind IStockMovementRepository.ExistsAsync: a redelivered
        // event that slipped past that check fails here instead of posting twice. SourceId is
        // the order item's PublicId.
        builder.HasIndex(m => new { m.SourceId, m.Reason, m.RawMaterialId }).IsUnique();
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
