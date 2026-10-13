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
    decimal? CostPerUnit,
    // Bought, or made in the kitchen (a prep item, its cost from its batches).
    MaterialKind Kind,
    // The purchase category Category names.
    Guid CategoryId);

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

// The Recipe Costing Template, for one recipe, or for a menu item, size or add-on by the recipe
// it sells by. Recipe is the name it goes by: its own, or its menu item's. MenuPrice is the price
// it is costed against: the menu's, or an off-menu recipe's planned price.
public sealed record RecipeCostCard(
    RecipeTargetKind? TargetKind,
    int? CatalogItemId,
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
    CostStatus Status,
    Guid? RecipeId = null,
    // Its own name, if it has one; the menu item's name, when it is linked to one.
    string? RecipeName = null,
    string? LinkedTo = null,
    // Off the menu: an off-menu or R&D recipe, never sold.
    bool IsStandalone = false,
    decimal? PlannedPrice = null,
    // A size with no recipe of its own, costed by the dish's.
    bool UsesBaseRecipe = false,
    bool IsDeleted = false,
    DateTime? DeletedAtUtc = null,
    string? DeletedBy = null,
    // The kitchen's own category for it; null until it is sorted.
    Guid? RecipeCategoryId = null,
    string? RecipeCategory = null,
    string? RecipeCategoryAr = null);

// Where a theoretical cost report's sales come from: the till's closed orders (and the online ones),
// or quantities an accountant typed in.
public enum TheoreticalSource
{
    Pos = 1,
    Manual = 2,
}

// One line of the report. QuantitySold is in portions, or for a prep item entered by hand in its
// Unit (kg, L). Off the menu, MenuItemId is 0 and RecipeId or PrepItemId says what it is.
public sealed record TheoreticalCostRow(
    string ItemCode,
    int MenuItemId,
    int? VariantId,
    string MenuItem,
    string? MenuItemAr,
    string Category,
    decimal QuantitySold,
    decimal NetSales,
    decimal RecipeCostPerUnit,
    decimal TheoreticalCost,
    decimal SharedCost,
    decimal? FoodCostPercent,
    CostStatus Status,
    int QuantityCostedNow,
    string? Unit = null,
    Guid? RecipeId = null,
    Guid? PrepItemId = null);

// The Monthly Theoretical Cost report: what the month's sales should have cost by their recipes,
// with no waste and no mistakes. Source says whose sales: the till's, costed as they were sold, or
// typed in, costed at today's prices; EnteredBy and EnteredAtUtc, for typed ones, by whom and when.
public sealed record TheoreticalCostReport(
    int Year,
    int Month,
    DateOnly From,
    DateOnly To,
    decimal FoodCostTargetPercent,
    IReadOnlyList<TheoreticalCostRow> Rows,
    decimal QuantitySold,
    decimal NetSales,
    decimal TheoreticalCost,
    decimal? FoodCostPercent,
    TheoreticalSource Source = TheoreticalSource.Pos,
    string? EnteredBy = null,
    DateTime? EnteredAtUtc = null);

// One row of the manual sales sheet: a recipe (in portions) or a prep item (in its display unit),
// what one costs now and sells at, and the quantity entered for the month, if any.
public sealed record ManualSalesItemDto(
    ManualSalesItemKind Kind,
    Guid ItemId,
    string? Code,
    string Name,
    string? NameAr,
    string Group,
    string Unit,
    decimal? CostPerUnit,
    decimal? Price,
    decimal? Quantity);

public sealed record ManualSalesSheetDto(int Year, int Month, IReadOnlyList<ManualSalesItemDto> Items, string? EnteredBy, DateTime? EnteredAtUtc);

public sealed record ManualSalesLineDto(ManualSalesItemKind Kind, Guid ItemId, decimal Quantity);

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
        // Deleted ones are kept for the records that name them, off the list.
        var materials = await db.RawMaterials.AsNoTracking().Where(m => m.ArchivedAtUtc == null).OrderBy(m => m.Name).ToListAsync(cancellationToken);
        var categories = await db.PurchaseCategories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        // The last price that stands: a voided delivery or batch, and its reversal, are not one.
        var lastPurchases = await db.StockMovements.AsNoTracking()
            .Where(m => (m.Reason == StockMovementReason.Purchase || m.Reason == StockMovementReason.ProductionOutput) && m.UnitCost != null
                && m.ReversalOf == null && !db.StockMovements.Any(r => r.ReversalOf == m.Id))
            .GroupBy(m => m.RawMaterialId)
            .Select(g => g.OrderByDescending(m => m.OccurredAtUtc).Select(m => new { m.RawMaterialId, m.UnitCost, m.OccurredAtUtc }).First())
            .ToDictionaryAsync(p => p.RawMaterialId, cancellationToken);

        return materials.Select(m =>
        {
            var last = lastPurchases.GetValueOrDefault(m.Id);
            return new MaterialCostDto(
                m.Id, m.Code, m.Name, categories.GetValueOrDefault(m.CategoryId), m.Unit, m.PurchaseUnit, m.PurchaseUnitSize,
                Money(m.CostPerPurchaseUnit), m.DefaultYieldPercent, m.QuantityOnHand, m.ReorderLevel,
                Money(m.AverageCost * Math.Max(m.QuantityOnHand, 0)),
                Money(last?.UnitCost * m.PurchaseUnitSize), last?.OccurredAtUtc, m.AverageCost, m.Kind, m.CategoryId);
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

    // Every dish, size and add-on with its cost per portion, by the recipe it sells by: what can be
    // linked, and which have no recipe yet.
    public async Task<IReadOnlyList<RecipeCostCard>> GetMenuCostsAsync(CancellationToken cancellationToken)
    {
        var data = await LoadMenuAsync(cancellationToken);
        return data.Targets.Select(t => Card(t, data)).ToList();
    }

    // Every recipe, on the menu or off it, by name: the recipes screen's list. The deleted ones
    // only when asked for.
    public async Task<IReadOnlyList<RecipeCostCard>> GetRecipesAsync(bool includeDeleted, CancellationToken cancellationToken)
    {
        var data = await LoadMenuAsync(cancellationToken);
        return data.AllRecipes
            .Where(r => includeDeleted || !r.IsDeleted)
            .Select(r => Card(r, data))
            .OrderBy(c => c.Recipe, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<RecipeCostCard> GetRecipeCardAsync(Guid recipeId, CancellationToken cancellationToken)
    {
        var data = await LoadMenuAsync(cancellationToken);
        var recipe = data.AllRecipes.SingleOrDefault(r => r.Id == recipeId) ?? throw new NotFoundException(nameof(Recipe), recipeId);
        return Card(recipe, data);
    }

    // ---- Pillar 2: the monthly theoretical cost report ----

    public async Task<TheoreticalCostReport> GetTheoreticalCostAsync(
        int year, int month, CancellationToken cancellationToken, TheoreticalSource source = TheoreticalSource.Pos)
    {
        if (month is < 1 or > 12 || year is < 2000 or > 2100)
            throw new DomainException("Choose a month.");
        if (source == TheoreticalSource.Manual)
            return await ManualTheoreticalCostAsync(year, month, cancellationToken);
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

    // ---- Pillar 2 by hand: sales typed in instead of the till's ----

    // Every active recipe and prep item, with what it costs and sells at now, and the quantity
    // entered for the month.
    public async Task<ManualSalesSheetDto> GetManualSalesAsync(int year, int month, CancellationToken cancellationToken)
    {
        if (month is < 1 or > 12 || year is < 2000 or > 2100)
            throw new DomainException("Choose a month.");
        var sheet = await ManualSheetAsync(year, month, cancellationToken);
        var entered = sheet?.Lines.ToDictionary(l => (l.Kind, l.ItemId), l => l.Quantity) ?? [];
        var items = await ManualItemsAsync(cancellationToken);
        return new ManualSalesSheetDto(
            year, month,
            items.Values
                .Select(i => i.Dto with { Quantity = entered.TryGetValue((i.Dto.Kind, i.Dto.ItemId), out var q) ? q : null })
                .OrderBy(i => i.Kind)
                .ThenBy(i => i.Group == "Uncategorized")
                .ThenBy(i => i.Group, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            sheet?.UpdatedBy, sheet?.UpdatedAtUtc);
    }

    // The month's quantities, the whole sheet each time: one left out or at 0 was not sold.
    // Portions are whole; a prep item's kg or litres need not be.
    public async Task<TheoreticalCostReport> SaveManualSalesAsync(
        int year, int month, IReadOnlyList<ManualSalesLineDto> lines, string? enteredBy, CancellationToken cancellationToken)
    {
        if (month is < 1 or > 12 || year is < 2000 or > 2100)
            throw new DomainException("Choose a month.");
        if (lines.GroupBy(l => (l.Kind, l.ItemId)).Any(g => g.Count() > 1))
            throw new DomainException("Each item goes on the sheet once.");
        var items = await ManualItemsAsync(cancellationToken);
        foreach (var line in lines)
        {
            if (!items.TryGetValue((line.Kind, line.ItemId), out var item))
                throw new DomainException("An item on the sheet is no longer there. Reload the sheet.");
            if (line.Quantity < 0)
                throw new DomainException($"{item.Dto.Name}: a quantity cannot be negative.");
            if (line.Kind == ManualSalesItemKind.Recipe && line.Quantity != decimal.Truncate(line.Quantity))
                throw new DomainException($"{item.Dto.Name}: portions are whole.");
        }

        var sheet = await db.ManualSalesSheets.Include(s => s.Lines).SingleOrDefaultAsync(s => s.Year == year && s.Month == month, cancellationToken);
        if (sheet is null)
            db.ManualSalesSheets.Add(sheet = new ManualSalesSheet { Year = year, Month = month });
        sheet.Lines.Clear();
        sheet.Lines.AddRange(lines.Where(l => l.Quantity > 0)
            .Select(l => new ManualSalesLine { Year = year, Month = month, Kind = l.Kind, ItemId = l.ItemId, Quantity = Math.Round(l.Quantity, 3) }));
        sheet.UpdatedAtUtc = DateTime.UtcNow;
        sheet.UpdatedBy = enteredBy;
        await db.SaveChangesAsync(cancellationToken);
        return await ManualTheoreticalCostAsync(year, month, cancellationToken);
    }

    // The same report from the typed quantities: each at what it costs by its recipe now, and the
    // month's shared costs spread over the meals typed in, as they are over the meals sold.
    private async Task<TheoreticalCostReport> ManualTheoreticalCostAsync(int year, int month, CancellationToken cancellationToken)
    {
        var settings = await SettingsAsync(cancellationToken);
        var first = new DateOnly(year, month, 1);
        var last = first.AddMonths(1).AddDays(-1);
        var sheet = await ManualSheetAsync(year, month, cancellationToken);
        var items = await ManualItemsAsync(cancellationToken);
        var lines = (sheet?.Lines ?? [])
            .Where(l => l.Quantity > 0 && items.ContainsKey((l.Kind, l.ItemId)))
            .Select(l => (Line: l, Item: items[(l.Kind, l.ItemId)]))
            .ToList();

        var meals = lines
            .Where(x => x.Item.IsMeal)
            .Select(x => new SoldLine(x.Item.MenuItemId, x.Item.VariantId, x.Item.Dto.Name, x.Item.Dto.NameAr, x.Item.SharedCategory ?? string.Empty,
                (int)x.Line.Quantity, (x.Item.Dto.Price ?? 0) * x.Line.Quantity, (x.Item.Dto.CostPerUnit ?? 0) * x.Line.Quantity, true, false, false))
            .ToList();
        var shared = await SharedPerMealAsync(first, last, meals, cancellationToken);
        var target = settings.FoodCostTargetPercent;

        var rows = lines
            .Select(x =>
            {
                var (line, item) = x;
                var quantity = line.Quantity;
                var sharedCost = item.IsMeal ? shared.For(item.SharedCategory) * quantity : 0;
                var theoretical = (item.Dto.CostPerUnit ?? 0) * quantity + sharedCost;
                var netSales = (item.Dto.Price ?? 0) * quantity;
                var foodCost = netSales > 0 ? Percent(theoretical / netSales) : (decimal?)null;
                var status = item.Dto.CostPerUnit is null || item.MissingPrice ? CostStatus.MissingPrices
                    : netSales <= 0 ? CostStatus.NoPrice
                    : foodCost > target ? CostStatus.AboveTarget : CostStatus.WithinTarget;
                return new TheoreticalCostRow(
                    item.Dto.Code ?? string.Empty, item.MenuItemId, item.VariantId, item.Dto.Name, item.Dto.NameAr, item.Dto.Group,
                    quantity, Money(netSales), Money(theoretical / quantity), Money(theoretical), Money(sharedCost), foodCost, status, 0,
                    item.Dto.Kind == ManualSalesItemKind.PrepItem ? item.Dto.Unit : null,
                    item.Dto.Kind == ManualSalesItemKind.Recipe ? item.Dto.ItemId : null,
                    item.Dto.Kind == ManualSalesItemKind.PrepItem ? item.Dto.ItemId : null);
            })
            .OrderBy(r => r.PrepItemId is not null)
            .ThenBy(r => r.Category == "Uncategorized")
            .ThenBy(r => r.Category, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.MenuItem, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var totalSales = rows.Sum(r => r.NetSales);
        var totalCost = rows.Sum(r => r.TheoreticalCost);
        return new TheoreticalCostReport(
            year, month, first, last, target, rows, rows.Where(r => r.Unit is null).Sum(r => r.QuantitySold), totalSales, totalCost,
            totalSales > 0 ? Percent(totalCost / totalSales) : null,
            TheoreticalSource.Manual, sheet?.UpdatedBy, sheet?.UpdatedAtUtc);
    }

    private Task<ManualSalesSheet?> ManualSheetAsync(int year, int month, CancellationToken cancellationToken) =>
        db.ManualSalesSheets.AsNoTracking().Include(s => s.Lines).SingleOrDefaultAsync(s => s.Year == year && s.Month == month, cancellationToken);

    // A row of the sheet with what the report needs of it besides: whether it is a meal (a dish on
    // the menu or off it, not an add-on or a prep item) and the category its shared costs go by.
    private sealed record ManualItem(ManualSalesItemDto Dto, bool IsMeal, string? SharedCategory, int MenuItemId, int? VariantId, bool MissingPrice);

    private async Task<IReadOnlyDictionary<(ManualSalesItemKind, Guid), ManualItem>> ManualItemsAsync(CancellationToken cancellationToken)
    {
        var data = await LoadMenuAsync(cancellationToken);
        var items = new Dictionary<(ManualSalesItemKind, Guid), ManualItem>();
        foreach (var recipe in data.AllRecipes.Where(r => !r.IsDeleted))
        {
            var card = Card(recipe, data);
            items[(ManualSalesItemKind.Recipe, recipe.Id)] = new ManualItem(
                new ManualSalesItemDto(
                    ManualSalesItemKind.Recipe, recipe.Id, string.IsNullOrEmpty(card.ItemCode) ? null : card.ItemCode, card.Recipe, card.RecipeAr,
                    card.RecipeCategory ?? "Uncategorized", "portion", card.Lines.Count > 0 ? card.IngredientCostPerPortion : null, card.MenuPrice, null),
                card.TargetKind != RecipeTargetKind.AddOn, card.Category, card.CatalogItemId ?? 0, card.VariantId, card.MissingPrices.Count > 0);
        }

        // A prep item at what a batch costs by its recipe now, as a dish is; with no recipe to go
        // by, at what the batches on the shelf cost.
        var prepRecipes = await db.PrepRecipes.AsNoTracking().Include(r => r.Ingredients).ToDictionaryAsync(r => r.Id, cancellationToken);
        foreach (var prep in data.Materials.Values.Where(m => m.IsPrepItem && !m.IsArchived))
        {
            decimal? perUnit = prep.AverageCost * prep.PurchaseUnitSize;
            if (prepRecipes.GetValueOrDefault(prep.Id) is { Ingredients.Count: > 0 } recipe
                && recipe.Ingredients.All(i => data.Materials.GetValueOrDefault(i.RawMaterialId)?.AverageCost is not null))
                perUnit = recipe.Ingredients.Sum(i => i.AsPurchasedQuantity * data.Materials[i.RawMaterialId].AverageCost!.Value)
                    / recipe.BatchQuantity * prep.PurchaseUnitSize;
            items[(ManualSalesItemKind.PrepItem, prep.Id)] = new ManualItem(
                new ManualSalesItemDto(
                    ManualSalesItemKind.PrepItem, prep.Id, prep.Code, prep.Name, null, "Prep items", prep.PurchaseUnit,
                    perUnit is { } p ? Money(p) : null, null, null),
                false, null, 0, null, false);
        }

        return items;
    }

    // ---- How it is worked out ----

    // A row of the menu that can have a recipe: a dish (or its base recipe), one of its sizes, an add-on.
    private sealed record Target(
        RecipeTargetKind Kind, int CatalogItemId, int? VariantId, string Name, string? NameAr, string? Category, decimal? Price, bool PricedByAddOns);

    private sealed record MenuData(
        IReadOnlyList<Target> Targets,
        // The recipe each target sells by: active and linked.
        IReadOnlyDictionary<RecipeTarget, Recipe> Recipes,
        IReadOnlyList<Recipe> AllRecipes,
        IReadOnlyDictionary<Guid, RecipeCategory> Categories,
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

        var allRecipes = await db.Recipes.AsNoTracking().Include(r => r.Ingredients).ToListAsync(cancellationToken);
        var recipes = allRecipes.Where(r => !r.IsDeleted && r.Target is not null).ToDictionary(r => r.Target!.Value);
        var materials = await db.RawMaterials.AsNoTracking().ToDictionaryAsync(m => m.Id, cancellationToken);

        // Shared costs on a recipe card are last month's, the last whole month there is.
        var thisMonth = clock.BusinessDateOf(DateTime.UtcNow);
        var lastFirst = new DateOnly(thisMonth.Year, thisMonth.Month, 1).AddMonths(-1);
        var lastLast = lastFirst.AddMonths(1).AddDays(-1);
        var shared = await SharedPerMealAsync(lastFirst, lastLast, await SalesAsync(lastFirst, lastLast, cancellationToken), cancellationToken);

        var categories = await db.RecipeCategories.AsNoTracking().ToDictionaryAsync(c => c.Id, cancellationToken);

        return new MenuData(targets, recipes, allRecipes, categories, materials, (await SettingsAsync(cancellationToken)).FoodCostTargetPercent, shared);
    }

    // A menu row, by the recipe it sells by: a size with none of its own, by the dish's.
    private static RecipeCostCard Card(Target target, MenuData data)
    {
        var own = data.Recipes.GetValueOrDefault(new RecipeTarget(target.Kind, target.CatalogItemId, target.VariantId));
        var recipe = own ?? (target.VariantId is not null ? data.Recipes.GetValueOrDefault(RecipeTarget.ForMenuItem(target.CatalogItemId)) : null);
        return Card(target, recipe, own is null && recipe is not null, data);
    }

    // A recipe itself: on the menu or off it, deleted or not.
    private static RecipeCostCard Card(Recipe recipe, MenuData data)
    {
        var target = recipe.Target is { } t
            ? data.Targets.SingleOrDefault(x => x.Kind == t.Kind && x.CatalogItemId == t.CatalogItemId && x.VariantId == t.VariantId)
            : null;
        return Card(target, recipe, false, data);
    }

    private static RecipeCostCard Card(Target? target, Recipe? recipe, bool usesBaseRecipe, MenuData data)
    {
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

        var standalone = recipe is { IsLinked: false };
        // The menu's category on the menu (shared costs are set by it); the kitchen's own off it.
        var own = recipe?.RecipeCategoryId is { } categoryId ? data.Categories.GetValueOrDefault(categoryId) : null;
        var category = target?.Category ?? own?.Name;
        var portions = recipe?.Portions ?? 1;
        var perRecipe = lines.Sum(l => l.RecipeCost ?? 0);
        var ingredientsPerPortion = perRecipe / portions;
        // A meal's share of the shared costs: a dish's, or a dish being planned off the menu; not an
        // add-on's. To the cent, as the card shows it: the till's live recalculation starts from it.
        var meal = target is null ? recipe is not null : target.Kind == RecipeTargetKind.MenuItem;
        var sharedPerPortion = meal && recipe is not null ? Money(data.Shared.For(category)) : 0;
        var perPortion = ingredientsPerPortion + sharedPerPortion;
        var price = target is null ? recipe?.PlannedPrice : target.PricedByAddOns ? null : target.Price;
        var foodCost = price > 0 ? Percent(perPortion / price.Value) : (decimal?)null;
        var missing = lines.Where(l => l.ApsPerUnit is null).Select(l => l.Ingredient).ToList();
        var status = recipe is null ? CostStatus.NoRecipe
            : missing.Count > 0 ? CostStatus.MissingPrices
            : foodCost is null ? CostStatus.NoPrice
            : foodCost > data.FoodCostTarget ? CostStatus.AboveTarget : CostStatus.WithinTarget;
        // Linked to a menu item that has since left the menu: named, but not costed against a price.
        var linkedTo = target?.Name ?? (recipe is { IsLinked: true } ? "A menu item no longer on the menu" : null);

        return new RecipeCostCard(
            target?.Kind ?? recipe?.TargetKind, target?.CatalogItemId ?? recipe?.CatalogItemId, target?.VariantId ?? recipe?.VariantId,
            target is null ? "" : target.Kind == RecipeTargetKind.AddOn ? $"A{target.CatalogItemId}" : ItemCode(target.CatalogItemId, target.VariantId),
            (usesBaseRecipe ? null : recipe?.Name) ?? target?.Name ?? recipe?.Name ?? "A recipe", target?.NameAr, category, recipe is not null, portions, price,
            data.FoodCostTarget, lines,
            Money(perRecipe), Money(ingredientsPerPortion), Money(sharedPerPortion), Money(perPortion),
            price is { } p ? Money(p - perPortion) : null, foodCost,
            Money(perPortion * 100 / data.FoodCostTarget), missing, status,
            recipe?.Id, usesBaseRecipe ? null : recipe?.Name, linkedTo, standalone, recipe?.PlannedPrice, usesBaseRecipe,
            recipe?.IsDeleted ?? false, recipe?.DeletedAtUtc, recipe?.DeletedBy,
            usesBaseRecipe ? null : own?.Id, usesBaseRecipe ? null : own?.Name, usesBaseRecipe ? null : own?.NameAr);
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
