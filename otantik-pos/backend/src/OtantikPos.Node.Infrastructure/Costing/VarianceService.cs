using Microsoft.EntityFrameworkCore;
using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Inventory.Domain.StockCounts;
using OtantikPos.Inventory.Domain.StockMovements;
using OtantikPos.Node.Infrastructure.Persistence;
using static OtantikPos.Node.Infrastructure.Costing.CostingService;

namespace OtantikPos.Node.Infrastructure.Costing;

public enum VarianceEvaluation
{
    // Within the tolerance either way: the template's ضمن الحد.
    WithinLimit,

    // More used than the recipes account for, beyond the tolerance (غير مواتٍ): waste, theft,
    // over-portioning, a recipe written too light, or a dish sold with no recipe.
    Unfavourable,

    // Less used than the recipes say (مواتٍ): under-portioning, a recipe written too heavy, or a
    // delivery that was never entered.
    Favourable,

    // A shared cost such as frying oil: used across meals, not by any recipe, so it has no
    // standard to measure against. Shown, but left out of the totals and the evaluation.
    SharedCost,
}

// One material between two counts, in its unit (grams, millilitres, pieces). The weekly
// template's columns (opening, received, damage, standard usage, expected, closing) and the
// actual-vs-standard template's (actual usage, variance, %, value, evaluation) in one row.
public sealed record VarianceRow(
    Guid RawMaterialId,
    string? Code,
    string Material,
    string? Category,
    UnitOfMeasure Unit,
    // How it is bought: a material bought by the gram is counted in grams, not kilograms.
    decimal PurchaseUnitSize,
    decimal Opening,
    decimal Received,
    // Spoilage recorded by hand: the template's damaged raw / packing.
    decimal RawWaste,
    // Dishes voided after the kitchen made them, less ingredients returned by refunds: damaged
    // product / returns.
    decimal ProductWaste,
    // What the sales took by their recipes, the recipe in force at each sale.
    decimal StandardUsage,
    decimal ExpectedClosing,
    decimal Closing,
    // Opening + received − closing.
    decimal ActualUsage,
    // Actual − standard: more than zero is a shortfall. It includes the recorded waste.
    decimal VarianceQuantity,
    decimal? VariancePercent,
    // Expected closing − closing: the part of the variance no record explains.
    decimal UnexplainedQuantity,
    // Average cost per unit at the closing count; null while the material has no price.
    decimal? UnitCost,
    decimal? VarianceValue,
    decimal? UnexplainedValue,
    VarianceEvaluation Evaluation);

public sealed record VarianceCount(Guid Id, DateTime PostedAtUtc, string? PostedBy);

public sealed record SoldWithoutStock(string ItemCode, string MenuItem, int Quantity);

public sealed record VarianceReport(
    VarianceCount From,
    VarianceCount To,
    decimal TolerancePercent,
    IReadOnlyList<VarianceRow> Rows,
    int UnfavourableCount,
    decimal NetVarianceValue,
    decimal RecordedWasteValue,
    decimal UnexplainedValue,
    string? LargestRelativeMaterial,
    decimal? LargestRelativePercent,
    // Counted in only one of the two counts, or moved in the period and counted in neither.
    IReadOnlyList<string> NotInBothCounts,
    // Sold with no recipe, so they took nothing from stock: their ingredients show as a shortfall.
    IReadOnlyList<SoldWithoutStock> SoldWithoutStock,
    IReadOnlyList<string> MissingPrices);

// One spoilage entry as it was recorded, its lines' worth at the cost then.
public sealed record SpoilageEntry(
    Guid SpoilageId, DateTime RecordedAtUtc, string? RecordedBy, IReadOnlyList<SpoilageEntryLine> Lines, decimal? Value);

public sealed record SpoilageEntryLine(
    Guid RawMaterialId, string Material, UnitOfMeasure Unit, decimal PurchaseUnitSize, decimal Quantity, string? Reason, decimal? Value);

// Pillar 3: actual usage against standard, between two posted stock counts. Actual usage is
// what the counts and the deliveries say left the stores; standard usage is what the recipes
// say the sales should have taken. Both are as purchased (before trimming), as stock is kept,
// so the variance is valued at the average purchase cost, as the template prices it.
public sealed class VarianceService(NodeDbContext db, SalesLedger ledger)
{
    private static readonly StockMovementReason[] Flows =
        [StockMovementReason.Purchase, StockMovementReason.Waste, StockMovementReason.VoidReturn, StockMovementReason.Spoilage];

    // Null ids: the last two posted counts; only toId: the count before it.
    public async Task<VarianceReport> GetVarianceAsync(Guid? fromId, Guid? toId, CancellationToken cancellationToken)
    {
        var posted = await db.StockCounts.AsNoTracking()
            .Where(c => c.Status == StockCountStatus.Posted)
            .OrderBy(c => c.PostedAtUtc)
            .Select(c => new VarianceCount(c.Id, c.PostedAtUtc!.Value, c.PostedBy))
            .ToListAsync(cancellationToken);

        var to = toId is { } t
            ? posted.Find(c => c.Id == t) ?? throw new DomainException("Choose a posted count to end at.")
            : posted.LastOrDefault() ?? throw new DomainException("Post a stock count first: the report measures between two counts.");
        var from = fromId is { } f
            ? posted.Find(c => c.Id == f) ?? throw new DomainException("Choose a posted count to start from.")
            : posted.LastOrDefault(c => c.PostedAtUtc < to.PostedAtUtc)
                ?? throw new DomainException("Post a second stock count: the report measures between two counts.");
        if (from.PostedAtUtc >= to.PostedAtUtc)
            throw new DomainException("The opening count must come before the closing one.");

        var settings = await db.CostingSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken) ?? new CostingSettings();
        var tolerance = settings.VarianceTolerancePercent;

        var lines = await db.Set<StockCountLine>().AsNoTracking()
            .Where(l => l.StockCountId == from.Id || l.StockCountId == to.Id)
            .ToListAsync(cancellationToken);
        var opening = lines.Where(l => l.StockCountId == from.Id).ToDictionary(l => l.RawMaterialId);
        var closing = lines.Where(l => l.StockCountId == to.Id).ToDictionary(l => l.RawMaterialId);
        var materials = await db.RawMaterials.AsNoTracking().ToDictionaryAsync(m => m.Id, cancellationToken);
        var shared = (await db.SharedCosts.AsNoTracking().Where(c => c.RawMaterialId != null).Select(c => c.RawMaterialId!.Value)
            .ToListAsync(cancellationToken)).ToHashSet();

        // Deliveries, waste, refunds and spoilage, by when they happened.
        var flows = await db.StockMovements.AsNoTracking()
            .Where(m => m.OccurredAtUtc > from.PostedAtUtc && m.OccurredAtUtc <= to.PostedAtUtc && Flows.Contains(m.Reason))
            .GroupBy(m => new { m.RawMaterialId, m.Reason })
            .Select(g => new { g.Key.RawMaterialId, g.Key.Reason, Quantity = g.Sum(m => m.Quantity) })
            .ToListAsync(cancellationToken);
        decimal Flow(Guid id, StockMovementReason reason) =>
            flows.Where(m => m.RawMaterialId == id && m.Reason == reason).Sum(m => m.Quantity);

        // Sales at the till by when their order closed, online ones by when they were paid: a
        // sale's movements are posted just after, so one closed a moment before the count belongs
        // to this period even if posted after it. After the opening count, up to the closing one.
        var sold = (await ledger.GetAsync(from.PostedAtUtc.AddTicks(1), to.PostedAtUtc.AddTicks(1), cancellationToken))
            .SelectMany(o => o.Lines)
            .ToList();
        var soldIds = sold.Select(i => i.PublicId).ToHashSet();
        var saleMovements = (await db.StockMovements.AsNoTracking()
                .Where(m => m.Reason == StockMovementReason.Sale
                    && m.OccurredAtUtc > from.PostedAtUtc.AddDays(-1) && m.OccurredAtUtc <= to.PostedAtUtc.AddDays(1))
                .Select(m => new { m.SourceId, m.RawMaterialId, m.Quantity })
                .ToListAsync(cancellationToken))
            .Where(m => soldIds.Contains(m.SourceId))
            .ToList();
        var standardBy = saleMovements.GroupBy(m => m.RawMaterialId).ToDictionary(g => g.Key, g => -g.Sum(m => m.Quantity));

        var withStock = saleMovements.Select(m => m.SourceId).ToHashSet();
        var soldWithoutStock = sold
            .Where(i => i.OnBill && !withStock.Contains(i.PublicId))
            .GroupBy(i => (i.MenuItemId, i.VariantId))
            .Select(g => new SoldWithoutStock(
                ItemCode(g.Key.MenuItemId, g.Key.VariantId),
                g.First().VariantName is { } size ? $"{g.First().MenuItemName} ({size})" : g.First().MenuItemName,
                g.Sum(i => i.Quantity)))
            .OrderByDescending(s => s.Quantity)
            .ToList();

        var rows = new List<VarianceRow>();
        foreach (var id in opening.Keys.Intersect(closing.Keys))
        {
            var material = materials[id];
            var open = opening[id].CountedQuantity;
            var close = closing[id].CountedQuantity;
            var received = Flow(id, StockMovementReason.Purchase);
            var rawWaste = -Flow(id, StockMovementReason.Spoilage);
            var productWaste = -(Flow(id, StockMovementReason.Waste) + Flow(id, StockMovementReason.VoidReturn));
            var standard = standardBy.GetValueOrDefault(id);
            var expected = open + received - rawWaste - productWaste - standard;
            var actual = open + received - close;
            var variance = actual - standard;
            var unexplained = expected - close;
            decimal? percent = standard > 0 ? Percent(variance / standard) : null;
            var unitCost = closing[id].UnitCost ?? opening[id].UnitCost ?? material.AverageCost;

            var evaluation = shared.Contains(id) && standard == 0 ? VarianceEvaluation.SharedCost
                : percent is { } p
                ? p > tolerance ? VarianceEvaluation.Unfavourable : p < -tolerance ? VarianceEvaluation.Favourable : VarianceEvaluation.WithinLimit
                // Nothing sold that uses it: any use at all is unexplained by sales.
                : variance > 0 ? VarianceEvaluation.Unfavourable : variance < 0 ? VarianceEvaluation.Favourable : VarianceEvaluation.WithinLimit;

            rows.Add(new VarianceRow(
                id, material.Code, material.Name, material.Category, material.Unit, material.PurchaseUnitSize,
                Quantity(open), Quantity(received), Quantity(rawWaste), Quantity(productWaste), Quantity(standard),
                Quantity(expected), Quantity(close), Quantity(actual), Quantity(variance), percent, Quantity(unexplained),
                unitCost, Money(variance * unitCost), Money(unexplained * unitCost), evaluation));
        }

        rows = rows
            .OrderBy(r => r.Evaluation == VarianceEvaluation.SharedCost)
            .ThenByDescending(r => Math.Abs(r.VarianceValue ?? 0))
            .ThenByDescending(r => Math.Abs(r.VariancePercent ?? 0))
            .ThenBy(r => r.Material)
            .ToList();

        var moved = flows.Select(m => m.RawMaterialId).Concat(standardBy.Keys);
        var notInBoth = opening.Keys.Union(closing.Keys).Union(moved)
            .Where(id => !(opening.ContainsKey(id) && closing.ContainsKey(id)))
            .Where(materials.ContainsKey)
            .Select(id => materials[id].Name)
            .Order()
            .ToList();

        // Shared costs are in the table but not the totals: they have no standard to fall short of.
        var measured = rows.Where(r => r.Evaluation != VarianceEvaluation.SharedCost).ToList();
        var largest = measured.Where(r => r.VariancePercent is not null).MaxBy(r => Math.Abs(r.VariancePercent!.Value));
        return new VarianceReport(
            from, to, tolerance, rows,
            measured.Count(r => r.Evaluation == VarianceEvaluation.Unfavourable),
            measured.Sum(r => r.VarianceValue ?? 0),
            Money(measured.Sum(r => (r.RawWaste + r.ProductWaste) * (r.UnitCost ?? 0))),
            measured.Sum(r => r.UnexplainedValue ?? 0),
            largest?.Material, largest?.VariancePercent,
            notInBoth, soldWithoutStock,
            rows.Where(r => r.UnitCost is null).Select(r => r.Material).ToList());
    }

    // Spoilage recorded in the period, newest first.
    public async Task<IReadOnlyList<SpoilageEntry>> GetSpoilageAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken)
    {
        var movements = await db.StockMovements.AsNoTracking()
            .Where(m => m.Reason == StockMovementReason.Spoilage && m.OccurredAtUtc >= fromUtc && m.OccurredAtUtc < toUtc)
            .ToListAsync(cancellationToken);
        var materials = await db.RawMaterials.AsNoTracking().ToDictionaryAsync(m => m.Id, cancellationToken);

        return movements
            .GroupBy(m => m.SourceId)
            .Select(g =>
            {
                var entryLines = g
                    .Select(m => new SpoilageEntryLine(
                        m.RawMaterialId, materials[m.RawMaterialId].Name, materials[m.RawMaterialId].Unit,
                        materials[m.RawMaterialId].PurchaseUnitSize, -m.Quantity, m.Note, Money(-m.Quantity * m.UnitCost)))
                    .OrderBy(l => l.Material)
                    .ToList();
                return new SpoilageEntry(
                    g.Key, g.Min(m => m.OccurredAtUtc), g.First().RecordedBy, entryLines,
                    entryLines.Any(l => l.Value is null) ? null : entryLines.Sum(l => l.Value));
            })
            .OrderByDescending(e => e.RecordedAtUtc)
            .ToList();
    }

    private static decimal Quantity(decimal value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);
}
