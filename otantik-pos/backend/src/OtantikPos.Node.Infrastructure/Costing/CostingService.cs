using Microsoft.EntityFrameworkCore;
using Otantik.BuildingBlocks;
using Otantik.SharedKernel.Catalog;
using Otantik.SharedKernel.Orders;
using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Inventory.Domain.Recipes;
using OtantikPos.Inventory.Domain.StockMovements;
using OtantikPos.Node.Infrastructure.Common;
using OtantikPos.Node.Infrastructure.Persistence;

namespace OtantikPos.Node.Infrastructure.Costing;

// Where a dish's cost stands against its price.
public enum CostStatus
{
    // Within the food cost target.
    WithinTarget,
    // Above it: the template's "فوق الهدف".
    AboveTarget,
    // No recipe, so no cost: never shown as a 0% food cost.
    NoRecipe,
    // A recipe with an ingredient that has no price yet: the cost shown is short of the truth.
    MissingPrices,
    // No price to compare with: priced by its add-ons, or free.
    NoPrice,
}

public sealed record MaterialCostDto(
    Guid Id,
    string? Code,
    string Name,
    string? Category,
    UnitOfMeasure Unit,
    string PurchaseUnit,
    decimal PurchaseUnitSize,
    decimal? CostPerPurchaseUnit,
    decimal DefaultYieldPercent,
    decimal QuantityOnHand,
    decimal ReorderLevel,
    decimal? StockValue,
    decimal? LastPurchaseCostPerPurchaseUnit,
    DateTime? LastPurchasedAtUtc,
    // The weighted average per unit (gram, millilitre, piece), unrounded: what a recipe card
    // works from, so the till's live figures match the server's to the cent.
    decimal? CostPerUnit);

// One line of the Recipe Costing Template. Quantity is the Edible Portion (EP) for the whole
// recipe in the material's Unit; APS/Unit and EPS/Unit are per PurchaseUnit, as prices are written.
public sealed record RecipeCostLine(
    Guid RawMaterialId,
    string? Code,
    string Ingredient,
    decimal Quantity,
    UnitOfMeasure Unit,
    string PurchaseUnit,
    decimal PurchaseUnitSize,
    decimal? ApsPerUnit,
    decimal YieldPercent,
    decimal? EpsPerUnit,
    decimal? RecipeCost);

// The Recipe Costing Template, for one menu item, size or add-on.
public sealed record RecipeCostCard(
    RecipeTargetKind TargetKind,
    int CatalogItemId,
    int? VariantId,
    string ItemCode,
    string Recipe,
    string? RecipeAr,
    string? Category,
    bool HasRecipe,
    int Portions,
    decimal? MenuPrice,
    decimal FoodCostTargetPercent,
    IReadOnlyList<RecipeCostLine> Lines,
    decimal CostPerRecipe,
    decimal IngredientCostPerPortion,
    decimal SharedCostPerPortion,
    decimal CostPerPortion,
    decimal? MarginPerPortion,
    decimal? FoodCostPercentActual,
    decimal IdealSellingPrice,
    IReadOnlyList<string> MissingPrices,
    CostStatus Status);

public sealed record TheoreticalCostRow(
    string ItemCode,
    int MenuItemId,
    int? VariantId,
    string MenuItem,
    string? MenuItemAr,
    string Category,
    int QuantitySold,
    decimal NetSales,
    decimal RecipeCostPerUnit,
    decimal TheoreticalCost,
    decimal SharedCost,
    decimal? FoodCostPercent,
    CostStatus Status,
    int QuantityCostedNow);

// The Monthly Theoretical Cost report: what the month's sales should have cost by their recipes,
// with no waste and no mistakes.
public sealed record TheoreticalCostReport(
    int Year,
    int Month,
    DateOnly From,
    DateOnly To,
    decimal FoodCostTargetPercent,
    IReadOnlyList<TheoreticalCostRow> Rows,
    int QuantitySold,
    decimal NetSales,
    decimal TheoreticalCost,
    decimal? FoodCostPercent);

// VarianceTolerancePercent and BeverageCategories: left out of a save, they stay as they are.
public sealed record CostingSettingsDto(
    decimal FoodCostTargetPercent, decimal? VarianceTolerancePercent = null, IReadOnlyList<string>? BeverageCategories = null);

public sealed record SharedCostDto(Guid Id, string Name, Guid? RawMaterialId, string? RawMaterialName, decimal? MonthlyAmount, string? Category);

public sealed record SaveSharedCostRequest(string Name, Guid? RawMaterialId, decimal? MonthlyAmount, string? Category);

// The manager's costing: the Recipe Costing Template (Pillar 1) and the Monthly Theoretical Cost
// report (Pillar 2). Costs come from the stock module: each material's weighted average cost,
// and the cost each sale's stock movements recorded when they happened. Prices are the menu's,
// which are before tax, so net sales need no VAT taken off.
public sealed class CostingService(NodeDbContext db, RestaurantClock clock, SalesLedger ledger)
{
    // ---- Materials ----

    public async Task<IReadOnlyList<MaterialCostDto>> GetMaterialsAsync(CancellationToken cancellationToken)
    {
        var materials = await db.RawMaterials.AsNoTracking().OrderBy(m => m.Name).ToListAsync(cancellationToken);
        var lastPurchases = await db.StockMovements.AsNoTracking()
            .Where(m => m.Reason == StockMovementReason.Purchase && m.UnitCost != null)
            .GroupBy(m => m.RawMaterialId)
            .Select(g => g.OrderByDescending(m => m.OccurredAtUtc).Select(m => new { m.RawMaterialId, m.UnitCost, m.OccurredAtUtc }).First())
            .ToDictionaryAsync(p => p.RawMaterialId, cancellationToken);

        return materials.Select(m =>
        {
            var last = lastPurchases.GetValueOrDefault(m.Id);
            return new MaterialCostDto(
                m.Id, m.Code, m.Name, m.Category, m.Unit, m.PurchaseUnit, m.PurchaseUnitSize,
                Money(m.CostPerPurchaseUnit), m.DefaultYieldPercent, m.QuantityOnHand, m.ReorderLevel,
                Money(m.AverageCost * Math.Max(m.QuantityOnHand, 0)),
                Money(last?.UnitCost * m.PurchaseUnitSize), last?.OccurredAtUtc, m.AverageCost);
        }).ToList();
    }

    // ---- Settings and shared costs ----

    public async Task<CostingSettingsDto> GetSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await SettingsAsync(cancellationToken);
        return new(settings.FoodCostTargetPercent, settings.VarianceTolerancePercent, settings.BeverageCategories);
    }

    public async Task<CostingSettingsDto> SaveSettingsAsync(CostingSettingsDto settings, CancellationToken cancellationToken)
    {
        if (settings.FoodCostTargetPercent is <= 0 or >= 100)
            throw new DomainException("The food cost target is a percentage between 0 and 100.");
        var row = await db.CostingSettings.SingleOrDefaultAsync(cancellationToken);
        if (row is null)
            db.CostingSettings.Add(row = new CostingSettings());
        if (settings.VarianceTolerancePercent is <= 0 or >= 100)
            throw new DomainException("The variance tolerance is a percentage between 0 and 100.");
        row.FoodCostTargetPercent = settings.FoodCostTargetPercent;
        row.VarianceTolerancePercent = settings.VarianceTolerancePercent ?? row.VarianceTolerancePercent;
        if (settings.BeverageCategories is { } beverages)
            row.BeverageCategories = beverages.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).Distinct().ToList();
        await db.SaveChangesAsync(cancellationToken);
        return new(row.FoodCostTargetPercent, row.VarianceTolerancePercent, row.BeverageCategories);
    }

    public async Task<IReadOnlyList<SharedCostDto>> GetSharedCostsAsync(CancellationToken cancellationToken)
    {
        var names = await db.RawMaterials.AsNoTracking().ToDictionaryAsync(m => m.Id, m => m.Name, cancellationToken);
        return (await db.SharedCosts.AsNoTracking().OrderBy(c => c.Name).ToListAsync(cancellationToken))
            .Select(c => new SharedCostDto(c.Id, c.Name, c.RawMaterialId, c.RawMaterialId is { } id ? names.GetValueOrDefault(id) : null, c.MonthlyAmount, c.Category))
            .ToList();
    }

    public async Task<SharedCostDto> SaveSharedCostAsync(Guid? id, SaveSharedCostRequest request, CancellationToken cancellationToken)
    {
        var cost = id is { } existing
            ? await db.SharedCosts.SingleOrDefaultAsync(c => c.Id == existing, cancellationToken) ?? throw new NotFoundException("Shared cost", existing)
            : db.SharedCosts.Add(new SharedCost()).Entity;
        cost.Name = request.Name ?? string.Empty;
        cost.RawMaterialId = request.RawMaterialId;
        cost.MonthlyAmount = request.MonthlyAmount;
        cost.Category = request.Category;
        cost.Validate();
        if (cost.RawMaterialId is { } materialId && !await db.RawMaterials.AnyAsync(m => m.Id == materialId, cancellationToken))
            throw new NotFoundException("Raw material", materialId);
        await db.SaveChangesAsync(cancellationToken);
        return (await GetSharedCostsAsync(cancellationToken)).Single(c => c.Id == cost.Id);
    }

    public async Task DeleteSharedCostAsync(Guid id, CancellationToken cancellationToken) =>
        await db.SharedCosts.Where(c => c.Id == id).ExecuteDeleteAsync(cancellationToken);

    // ---- Pillar 1: the recipe costing card ----

    public async Task<RecipeCostCard> GetRecipeCardAsync(RecipeTargetKind kind, int catalogItemId, int? variantId, CancellationToken cancellationToken)
    {
        var data = await LoadMenuAsync(cancellationToken);
        var target = data.Targets.SingleOrDefault(t => t.Kind == kind && t.CatalogItemId == catalogItemId && t.VariantId == variantId)
            ?? throw new NotFoundException("Menu item", variantId is null ? catalogItemId : $"{catalogItemId}/{variantId}");
        return Card(target, data);
    }

    // Every dish, size and add-on with its cost per portion: the recipes screen's list.
    public async Task<IReadOnlyList<RecipeCostCard>> GetMenuCostsAsync(CancellationToken cancellationToken)
    {
        var data = await LoadMenuAsync(cancellationToken);
        return data.Targets.Select(t => Card(t, data)).ToList();
    }

    // ---- Pillar 2: the monthly theoretical cost report ----

    public async Task<TheoreticalCostReport> GetTheoreticalCostAsync(int year, int month, CancellationToken cancellationToken)
    {
        if (month is < 1 or > 12 || year is < 2000 or > 2100)
            throw new DomainException("Choose a month.");
        var settings = await SettingsAsync(cancellationToken);
        var first = new DateOnly(year, month, 1);
        var last = first.AddMonths(1).AddDays(-1);
        var sales = await SalesAsync(first, last, cancellationToken);
        var shared = await SharedPerMealAsync(first, last, sales, cancellationToken);
        var target = settings.FoodCostTargetPercent;

        var rows = sales
            .GroupBy(s => (s.MenuItemId, s.VariantId))
            .Select(g =>
            {
                var any = g.First();
                var quantity = g.Sum(s => s.Quantity);
                var netSales = g.Sum(s => s.NetSales);
                var ingredients = g.Sum(s => s.IngredientCost);
                var sharedCost = shared.For(any.Category) * quantity;
                var theoretical = ingredients + sharedCost;
                var foodCost = netSales > 0 ? Percent(theoretical / netSales) : (decimal?)null;
                var status = g.All(s => !s.HasRecipe) ? CostStatus.NoRecipe
                    : g.Any(s => s.MissingPrice) ? CostStatus.MissingPrices
                    : netSales <= 0 ? CostStatus.NoPrice
                    : foodCost > target ? CostStatus.AboveTarget : CostStatus.WithinTarget;
                return new TheoreticalCostRow(
                    ItemCode(any.MenuItemId, any.VariantId), any.MenuItemId, any.VariantId, any.Name, any.NameAr, any.Category,
                    quantity, Money(netSales), Money(theoretical / quantity), Money(theoretical), Money(sharedCost), foodCost, status,
                    g.Where(s => s.CostedNow).Sum(s => s.Quantity));
            })
            .OrderBy(r => r.MenuItemId).ThenBy(r => r.VariantId)
            .ToList();

        var totalSales = rows.Sum(r => r.NetSales);
        var totalCost = rows.Sum(r => r.TheoreticalCost);
        return new TheoreticalCostReport(
            year, month, first, last, target, rows, rows.Sum(r => r.QuantitySold), totalSales, totalCost,
            totalSales > 0 ? Percent(totalCost / totalSales) : null);
    }

    // ---- How it is worked out ----

    // A row of the menu that can have a recipe: a dish (or its base recipe), one of its sizes, an add-on.
    private sealed record Target(
        RecipeTargetKind Kind, int CatalogItemId, int? VariantId, string Name, string? NameAr, string? Category, decimal? Price, bool PricedByAddOns);

    private sealed record MenuData(
        IReadOnlyList<Target> Targets,
        IReadOnlyDictionary<RecipeTarget, Recipe> Recipes,
        IReadOnlyDictionary<Guid, RawMaterial> Materials,
        decimal FoodCostTarget,
        SharedPerMeal Shared);

    private async Task<MenuData> LoadMenuAsync(CancellationToken cancellationToken)
    {
        var items = await db.MenuItems.AsNoTracking().Where(m => !m.IsDeleted).OrderBy(m => m.Id).ToListAsync(cancellationToken);
        var variants = (await db.MenuItemVariants.AsNoTracking().OrderBy(v => v.DisplayOrder).ThenBy(v => v.Id).ToListAsync(cancellationToken))
            .ToLookup(v => v.MenuItemId);
        var addOns = await db.AddOns.AsNoTracking().OrderBy(a => a.Id).ToListAsync(cancellationToken);

        var targets = new List<Target>();
        foreach (var item in items)
        {
            // A dish with sizes is sold by size; its base recipe is what a size without its own uses.
            targets.Add(new Target(RecipeTargetKind.MenuItem, item.Id, null, item.Name, item.NameAr, item.Category,
                variants[item.Id].Any() ? null : item.Price, item.IsPriceBasedOnAddons));
            foreach (var variant in variants[item.Id])
                targets.Add(new Target(RecipeTargetKind.MenuItem, item.Id, variant.Id, $"{item.Name} ({variant.Name})",
                    item.NameAr is null ? null : $"{item.NameAr} ({variant.NameAr ?? variant.Name})", item.Category, variant.Price, false));
        }
        targets.AddRange(addOns.Select(a => new Target(RecipeTargetKind.AddOn, a.Id, null, a.Name, a.NameAr, null, a.Price, false)));

        var recipes = (await db.Recipes.AsNoTracking().Include(r => r.Ingredients).ToListAsync(cancellationToken)).ToDictionary(r => r.Target);
        var materials = await db.RawMaterials.AsNoTracking().ToDictionaryAsync(m => m.Id, cancellationToken);

        // Shared costs on a recipe card are last month's, the last whole month there is.
        var thisMonth = clock.BusinessDateOf(DateTime.UtcNow);
        var lastFirst = new DateOnly(thisMonth.Year, thisMonth.Month, 1).AddMonths(-1);
        var lastLast = lastFirst.AddMonths(1).AddDays(-1);
        var shared = await SharedPerMealAsync(lastFirst, lastLast, await SalesAsync(lastFirst, lastLast, cancellationToken), cancellationToken);

        return new MenuData(targets, recipes, materials, (await SettingsAsync(cancellationToken)).FoodCostTargetPercent, shared);
    }

    private static RecipeCostCard Card(Target target, MenuData data)
    {
        var key = new RecipeTarget(target.Kind, target.CatalogItemId, target.VariantId);
        // A size with no recipe of its own uses the dish's base recipe, as a sale does.
        var recipe = data.Recipes.GetValueOrDefault(key)
            ?? (target.VariantId is not null ? data.Recipes.GetValueOrDefault(RecipeTarget.ForMenuItem(target.CatalogItemId)) : null);

        var lines = recipe?.Ingredients
            .Select(i =>
            {
                var m = data.Materials[i.RawMaterialId];
                var aps = m.CostPerPurchaseUnit;
                var eps = aps * 100 / i.YieldPercent;
                return new RecipeCostLine(m.Id, m.Code, m.Name, i.Quantity, m.Unit, m.PurchaseUnit, m.PurchaseUnitSize,
                    Money(aps), i.YieldPercent, Money(eps), Money(eps * i.Quantity / m.PurchaseUnitSize));
            })
            .OrderBy(l => l.Ingredient)
            .ToList() ?? [];

        var portions = recipe?.Portions ?? 1;
        var perRecipe = lines.Sum(l => l.RecipeCost ?? 0);
        var ingredientsPerPortion = perRecipe / portions;
        // To the cent, as the card shows it: the till's live recalculation starts from that figure.
        var sharedPerPortion = target.Kind == RecipeTargetKind.MenuItem && recipe is not null ? Money(data.Shared.For(target.Category)) : 0;
        var perPortion = ingredientsPerPortion + sharedPerPortion;
        var price = target.PricedByAddOns ? null : target.Price;
        var foodCost = price > 0 ? Percent(perPortion / price.Value) : (decimal?)null;
        var missing = lines.Where(l => l.ApsPerUnit is null).Select(l => l.Ingredient).ToList();
        var status = recipe is null ? CostStatus.NoRecipe
            : missing.Count > 0 ? CostStatus.MissingPrices
            : foodCost is null ? CostStatus.NoPrice
            : foodCost > data.FoodCostTarget ? CostStatus.AboveTarget : CostStatus.WithinTarget;

        return new RecipeCostCard(
            target.Kind, target.CatalogItemId, target.VariantId,
            target.Kind == RecipeTargetKind.AddOn ? $"A{target.CatalogItemId}" : ItemCode(target.CatalogItemId, target.VariantId),
            target.Name, target.NameAr, target.Category, recipe is not null, portions, price, data.FoodCostTarget, lines,
            Money(perRecipe), Money(ingredientsPerPortion), Money(sharedPerPortion), Money(perPortion),
            price is { } p ? Money(p - perPortion) : null, foodCost,
            Money(perPortion * 100 / data.FoodCostTarget), missing, status);
    }

    // One sold line of a settled order in the month, net of refunds, with what its ingredients
    // cost when it was sold: the stock movements its sale posted. CostedNow: sold before its dish
    // had a recipe, so costed by today's recipe at today's prices instead.
    private sealed record SoldLine(
        int MenuItemId, int? VariantId, string Name, string? NameAr, string Category, int Quantity, decimal NetSales,
        decimal IngredientCost, bool HasRecipe, bool MissingPrice, bool CostedNow);

    private async Task<IReadOnlyList<SoldLine>> SalesAsync(DateOnly first, DateOnly last, CancellationToken cancellationToken)
    {
        var (from, _) = clock.Bounds(first);
        var (_, to) = clock.Bounds(last);

        // Paid at the till or online, billed and not refunded: a line voided before payment was
        // never charged, and a refunded one is not a sale. An order's own discount is not shared
        // out across its items.
        var lines = (await ledger.GetAsync(from, to, cancellationToken)).SelectMany(o => o.Lines).Where(l => l.OnBill).ToList();
        if (lines.Count == 0)
            return [];

        // A sale's movements are posted just after the order closes; a day either side catches
        // the ones at the month's edges, and the line's id picks out its own.
        var ids = lines.Select(l => l.PublicId).ToHashSet();
        var movements = (await db.StockMovements.AsNoTracking()
                .Where(m => m.Reason == StockMovementReason.Sale && m.OccurredAtUtc >= from.AddDays(-1) && m.OccurredAtUtc < to.AddDays(1))
                .Select(m => new { m.SourceId, m.Quantity, m.UnitCost })
                .ToListAsync(cancellationToken))
            .Where(m => ids.Contains(m.SourceId))
            .ToLookup(m => m.SourceId);

        // A line sold before its dish had a recipe posted no movements. Once the dish has one, the
        // line is costed as the same sale would be today: the recipes a sale draws on
        // (StockRequirements), at each material's average cost now.
        var recipes = await db.Recipes.AsNoTracking().Include(r => r.Ingredients).ToListAsync(cancellationToken);
        var averageCosts = await db.RawMaterials.AsNoTracking().ToDictionaryAsync(m => m.Id, m => m.AverageCost, cancellationToken);

        var menu = await db.MenuItems.AsNoTracking().IgnoreQueryFilters()
            .Select(m => new { m.Id, m.NameAr, m.Category })
            .ToDictionaryAsync(m => m.Id, cancellationToken);
        var variantNamesAr = await db.MenuItemVariants.AsNoTracking().ToDictionaryAsync(v => v.Id, v => v.NameAr, cancellationToken);

        return lines.Select(l =>
        {
            var posted = movements[l.PublicId].ToList();
            var item = menu.GetValueOrDefault(l.MenuItemId);
            var nameAr = item?.NameAr is null ? null
                : l.VariantId is { } v ? $"{item.NameAr} ({variantNamesAr.GetValueOrDefault(v) ?? l.VariantName})" : item.NameAr;
            var name = l.VariantName is null ? l.MenuItemName : $"{l.MenuItemName} ({l.VariantName})";
            if (posted.Count > 0)
                return new SoldLine(l.MenuItemId, l.VariantId, name, nameAr, item?.Category ?? string.Empty, l.Quantity, l.LineTotal,
                    posted.Sum(m => -m.Quantity * (m.UnitCost ?? 0)), true, posted.Any(m => m.UnitCost is null), false);

            var needs = StockRequirements.For(new SoldItem(l.MenuItemId, l.VariantId, l.AddOnIds, l.Quantity), recipes);
            return new SoldLine(l.MenuItemId, l.VariantId, name, nameAr, item?.Category ?? string.Empty, l.Quantity, l.LineTotal,
                needs.Sum(n => n.Value * (averageCosts.GetValueOrDefault(n.Key) ?? 0)), needs.Count > 0,
                needs.Keys.Any(id => averageCosts.GetValueOrDefault(id) is null), needs.Count > 0);
        }).ToList();
    }

    // Each shared cost's month, per meal sold that shares it. A meal is food: a shared cost for
    // every meal (frying oil, gas, takeaway boxes) is not charged to a drink. One named for a
    // drinks category is, to that category.
    private sealed class SharedPerMeal(decimal everyMeal, IReadOnlyDictionary<string, decimal> byCategory, IReadOnlySet<string> drinks)
    {
        public static readonly SharedPerMeal None = new(0, new Dictionary<string, decimal>(), new HashSet<string>());

        public decimal For(string? category) =>
            (category is not null && drinks.Contains(category) ? 0 : everyMeal)
            + (category is not null ? byCategory.GetValueOrDefault(category) : 0);
    }

    private async Task<SharedPerMeal> SharedPerMealAsync(DateOnly first, DateOnly last, IReadOnlyList<SoldLine> sales, CancellationToken cancellationToken)
    {
        var costs = await db.SharedCosts.AsNoTracking().ToListAsync(cancellationToken);
        if (costs.Count == 0 || sales.Count == 0)
            return SharedPerMeal.None;

        var (from, _) = clock.Bounds(first);
        var (_, to) = clock.Bounds(last);
        var materialIds = costs.Where(c => c.RawMaterialId is not null).Select(c => c.RawMaterialId!.Value).ToList();
        var purchased = await db.StockMovements.AsNoTracking()
            .Where(m => m.Reason == StockMovementReason.Purchase && materialIds.Contains(m.RawMaterialId)
                && m.OccurredAtUtc >= from && m.OccurredAtUtc < to)
            .GroupBy(m => m.RawMaterialId)
            .Select(g => new { RawMaterialId = g.Key, Cost = g.Sum(m => m.Quantity * (m.UnitCost ?? 0)) })
            .ToDictionaryAsync(p => p.RawMaterialId, p => p.Cost, cancellationToken);

        var drinks = (await SettingsAsync(cancellationToken)).BeverageCategories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var meals = sales.Where(s => !drinks.Contains(s.Category)).Sum(s => s.Quantity);
        var mealsIn = sales.GroupBy(s => s.Category).ToDictionary(g => g.Key, g => g.Sum(s => s.Quantity));
        decimal everyMeal = 0;
        var byCategory = new Dictionary<string, decimal>();
        foreach (var cost in costs)
        {
            var month = cost.MonthlyAmount ?? purchased.GetValueOrDefault(cost.RawMaterialId!.Value);
            if (cost.Category is null)
                everyMeal += meals > 0 ? month / meals : 0;
            else if (mealsIn.GetValueOrDefault(cost.Category) is > 0 and var inCategory)
                byCategory[cost.Category] = byCategory.GetValueOrDefault(cost.Category) + month / inCategory;
        }
        return new SharedPerMeal(everyMeal, byCategory, drinks);
    }

    private async Task<CostingSettings> SettingsAsync(CancellationToken cancellationToken) =>
        await db.CostingSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken) ?? new CostingSettings();

    // The menu's own numbers: the dish's, and the size's after it.
    internal static string ItemCode(int menuItemId, int? variantId) =>
        variantId is { } v ? $"{menuItemId}-{v}" : menuItemId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal static decimal Money(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    internal static decimal? Money(decimal? value) => value is { } v ? Money(v) : null;

    internal static decimal Percent(decimal share) => Math.Round(share * 100, 1, MidpointRounding.AwayFromZero);
}
