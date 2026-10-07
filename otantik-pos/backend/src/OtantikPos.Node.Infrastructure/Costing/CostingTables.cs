using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Node.Infrastructure.Persistence;

namespace OtantikPos.Node.Infrastructure.Costing;

// The food cost a manager aims at, the Food Cost % Budget on a recipe card: 30% unless set.
// One row. A dish costing more than this share of its price is "above target".
public sealed class CostingSettings
{
    public const int SingletonId = 1;
    public const decimal DefaultFoodCostTarget = 30;

    public int Id { get; init; } = SingletonId;
    public decimal FoodCostTargetPercent { get; set; } = DefaultFoodCostTarget;
}

// A cost no recipe names, spread over the meals that share it: frying oil, gas, takeaway boxes.
// Each month's total is either what that month's purchases of a raw material cost (the oil), or
// a fixed amount; it is shared out per meal sold in the month, across the whole menu or one
// category. The template's "total monthly oil cost ÷ total monthly meals sold".
public sealed class SharedCost
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;

    // One of the two.
    public Guid? RawMaterialId { get; set; }
    public decimal? MonthlyAmount { get; set; }

    // A menu category's name; null for every meal.
    public string? Category { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new DomainException("Name the shared cost, such as Frying oil.");
        if ((RawMaterialId is null) == (MonthlyAmount is null))
            throw new DomainException("A shared cost comes from a raw material's purchases or a fixed monthly amount, one of the two.");
        if (MonthlyAmount < 0)
            throw new DomainException("A monthly amount cannot be negative.");
        Name = Name.Trim();
        Category = string.IsNullOrWhiteSpace(Category) ? null : Category.Trim();
    }
}

internal sealed class CostingSettingsConfiguration : IEntityTypeConfiguration<CostingSettings>
{
    public void Configure(EntityTypeBuilder<CostingSettings> builder)
    {
        builder.ToTable("CostingSettings", NodeDbContext.InventorySchema);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.FoodCostTargetPercent).HasPrecision(5, 2);
    }
}

internal sealed class SharedCostConfiguration : IEntityTypeConfiguration<SharedCost>
{
    public void Configure(EntityTypeBuilder<SharedCost> builder)
    {
        builder.ToTable("SharedCosts", NodeDbContext.InventorySchema);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Name).HasMaxLength(100);
        builder.Property(c => c.Category).HasMaxLength(100);
        builder.Property(c => c.MonthlyAmount).HasPrecision(18, 2);
        builder.HasOne<RawMaterial>().WithMany().HasForeignKey(c => c.RawMaterialId).OnDelete(DeleteBehavior.Restrict);
    }
}
