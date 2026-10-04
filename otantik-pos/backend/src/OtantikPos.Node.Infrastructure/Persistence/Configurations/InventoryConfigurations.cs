using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Inventory.Domain.Recipes;
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
    }
}

internal sealed class RecipeConfiguration : IEntityTypeConfiguration<Recipe>
{
    public void Configure(EntityTypeBuilder<Recipe> builder)
    {
        builder.ToTable("Recipes", NodeDbContext.InventorySchema);
        builder.Ignore(r => r.Target);
        builder.Property(r => r.TargetKind).IsEnumName();

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
        builder.Property(i => i.QuantityPerPortion).IsQuantity();

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
