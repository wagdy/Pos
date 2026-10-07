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

    public const decimal DefaultVarianceTolerance = 5;

    public int Id { get; init; } = SingletonId;
    public decimal FoodCostTargetPercent { get; set; } = DefaultFoodCostTarget;

    // How far actual usage may stray from standard, either way, before the variance report calls
    // it unfavourable or favourable: the template's ±5%.
    public decimal VarianceTolerancePercent { get; set; } = DefaultVarianceTolerance;

    // The menu categories that are drinks, by name: their sales and costs are the beverage cost %,
    // the rest the food cost %. None set, everything is food.
    public List<string> BeverageCategories { get; set; } = [];

    // The break-even worksheet's four options: weekly sales to try against this year's costs.
    // Empty until the manager sets them.
    public List<decimal> BreakEvenScenarios { get; set; } = [];
}

// What a month cost beyond its ingredients, as the manager enters it: what the KPIs need besides
// sales, and the income statement's lines for the break-even worksheet. Every figure is optional
// until known; a KPI that needs a missing one says so rather than showing 0.
public sealed class MonthlyExpenses
{
    public int Year { get; init; }
    public int Month { get; init; }

    // Wages, salaries and staff benefits: the labour cost %.
    public decimal? WagesAndBenefits { get; set; }

    // Hours worked by all staff in the month: sales per labour hour.
    public decimal? LabourHours { get; set; }

    // Utilities, marketing, repairs, supplies: the rest of the controllable costs.
    public decimal? OtherControllableCosts { get; set; }

    // Rent and the like.
    public decimal? OccupationCost { get; set; }

    public decimal? Interest { get; set; }
    public decimal? Depreciation { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }

    public void Validate()
    {
        if (Month is < 1 or > 12 || Year is < 2000 or > 2100)
            throw new DomainException("Choose a month.");
        if (new[] { WagesAndBenefits, LabourHours, OtherControllableCosts, OccupationCost, Interest, Depreciation }.Any(v => v < 0))
            throw new DomainException("A month's costs and hours cannot be negative.");
    }
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
        builder.Property(s => s.VarianceTolerancePercent).HasPrecision(5, 2).HasDefaultValue(CostingSettings.DefaultVarianceTolerance);
        builder.Property(s => s.BeverageCategories).HasDefaultValueSql("'{}'::text[]");
        builder.Property(s => s.BreakEvenScenarios).HasDefaultValueSql("'{}'::numeric[]");
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

internal sealed class MonthlyExpensesConfiguration : IEntityTypeConfiguration<MonthlyExpenses>
{
    public void Configure(EntityTypeBuilder<MonthlyExpenses> builder)
    {
        builder.ToTable("MonthlyExpenses", NodeDbContext.InventorySchema);
        builder.HasKey(e => new { e.Year, e.Month });
        builder.Property(e => e.WagesAndBenefits).HasPrecision(18, 2);
        builder.Property(e => e.LabourHours).HasPrecision(10, 2);
        builder.Property(e => e.OtherControllableCosts).HasPrecision(18, 2);
        builder.Property(e => e.OccupationCost).HasPrecision(18, 2);
        builder.Property(e => e.Interest).HasPrecision(18, 2);
        builder.Property(e => e.Depreciation).HasPrecision(18, 2);
        builder.Property(e => e.UpdatedBy).HasMaxLength(200);
    }
}
