using Microsoft.EntityFrameworkCore;
using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Domain.Categories;
using OtantikPos.Inventory.Domain.Purchases;
using OtantikPos.Inventory.Domain.Treasury;
using OtantikPos.Node.Infrastructure.Common;
using OtantikPos.Node.Infrastructure.Persistence;
using static OtantikPos.Node.Infrastructure.Costing.CostingService;

namespace OtantikPos.Node.Infrastructure.Costing;

// One material or expense item's purchases in the period. Quantity is in what the item is bought
// in (kg, carton) for a material, and as entered for an expense; Unit names it.
public sealed record ItemSpending(PurchaseItemType ItemType, Guid ItemId, string Name, decimal Quantity, string Unit, decimal Total, int Lines);

// A category's purchases: what went into stock, what was expenses, and its share of the whole.
public sealed record CategorySpending(
    Guid CategoryId,
    string Category,
    string? CategoryAr,
    ExpenseLine ExpenseLine,
    decimal Inventory,
    decimal Expenses,
    decimal Total,
    decimal? SharePercent,
    IReadOnlyList<ItemSpending> Items);

// Purchases by category over a period, from the posted invoices: each invoice on its own date, or
// on the day it was posted when it has none; and the expense vouchers paid from the safe, each on
// its date. Lines with no price are counted, not valued.
public sealed record SpendingReport(
    DateOnly From,
    DateOnly To,
    decimal Total,
    decimal Inventory,
    decimal Expenses,
    int Invoices,
    int UnpricedLines,
    IReadOnlyList<CategorySpending> Categories,
    int Vouchers = 0);

// What a period's expense lines came to in one category, for the profit and loss.
public sealed record LoggedExpense(Guid CategoryId, string Category, string? CategoryAr, ExpenseLine ExpenseLine, decimal Amount);

// Purchases added up by category: the Purchases by Category report, and the logged expenses the
// KPIs and the break-even worksheet take off as overheads. Each line is filed under its item's
// category as it is now, so moving an item to the right category puts its history right too.
public sealed class PurchaseSpendingService(NodeDbContext db, RestaurantClock clock)
{
    // InvoiceId: the invoice's, or an expense voucher's (IsVoucher).
    private sealed record PostedLine(
        Guid InvoiceId, PurchaseItemType ItemType, Guid ItemId, string Name, PurchaseCategory? Category, decimal Quantity, string Unit, decimal? Cost,
        bool IsVoucher = false);

    public async Task<SpendingReport> GetAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        if (to < from)
            throw new DomainException("The first day must come before the last.");
        if (to > from.AddYears(5))
            throw new DomainException("Choose five years at most.");

        var lines = await PostedLinesAsync(from, to, cancellationToken);
        var priced = lines.Where(l => l.Cost is not null).ToList();
        var total = priced.Sum(l => l.Cost!.Value);
        var categories = priced
            .GroupBy(l => l.Category?.Id ?? PurchaseCategory.GeneralId)
            .Select(g =>
            {
                var category = g.First().Category;
                var inventory = g.Where(l => l.ItemType == PurchaseItemType.Inventory).Sum(l => l.Cost!.Value);
                var expenses = g.Where(l => l.ItemType == PurchaseItemType.Expense).Sum(l => l.Cost!.Value);
                return new CategorySpending(
                    g.Key, category?.Name ?? PurchaseCategory.GeneralName, category?.NameAr, category?.ExpenseLine ?? ExpenseLine.Controllable,
                    Money(inventory), Money(expenses), Money(inventory + expenses),
                    total > 0 ? Percent((inventory + expenses) / total) : null,
                    g.GroupBy(l => (l.ItemType, l.ItemId))
                        .Select(i => new ItemSpending(
                            i.Key.ItemType, i.Key.ItemId, i.First().Name, Math.Round(i.Sum(l => l.Quantity), 3), i.First().Unit,
                            Money(i.Sum(l => l.Cost!.Value)), i.Count()))
                        .OrderByDescending(i => i.Total)
                        .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
                        .ToList());
            })
            .OrderByDescending(c => c.Total)
            .ThenBy(c => c.Category, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new SpendingReport(
            from, to, Money(total),
            Money(priced.Where(l => l.ItemType == PurchaseItemType.Inventory).Sum(l => l.Cost!.Value)),
            Money(priced.Where(l => l.ItemType == PurchaseItemType.Expense).Sum(l => l.Cost!.Value)),
            lines.Where(l => !l.IsVoucher).Select(l => l.InvoiceId).Distinct().Count(),
            lines.Count - priced.Count,
            categories,
            lines.Count(l => l.IsVoucher));
    }

    // The period's expense lines by category: overheads for the profit and loss. Stock lines are
    // left out; they are cost of sales once the recipes use them.
    public async Task<IReadOnlyList<LoggedExpense>> LoggedExpensesAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        (await PostedLinesAsync(from, to, cancellationToken))
            .Where(l => l.ItemType == PurchaseItemType.Expense && l.Cost is not null)
            .GroupBy(l => l.Category?.Id ?? PurchaseCategory.GeneralId)
            .Select(g => new LoggedExpense(
                g.Key, g.First().Category?.Name ?? PurchaseCategory.GeneralName, g.First().Category?.NameAr,
                g.First().Category?.ExpenseLine ?? ExpenseLine.Controllable, Money(g.Sum(l => l.Cost!.Value))))
            .OrderByDescending(e => e.Amount)
            .ToList();

    private async Task<IReadOnlyList<PostedLine>> PostedLinesAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var (fromUtc, _) = clock.Bounds(from);
        var (_, toUtc) = clock.Bounds(to);
        var invoices = await db.PurchaseInvoices.AsNoTracking().Include(i => i.Lines)
            .Where(i => i.Status == PurchaseInvoiceStatus.Posted
                && ((i.InvoiceDate != null && i.InvoiceDate >= from && i.InvoiceDate <= to)
                    || (i.InvoiceDate == null && i.PostedAtUtc >= fromUtc && i.PostedAtUtc < toUtc)))
            .ToListAsync(cancellationToken);
        // Expense vouchers that stand: a voided one leaves the reports of its own date.
        var vouchers = await db.SafeTransactions.AsNoTracking()
            .Where(t => t.Kind == SafeTransactionKind.ExpenseVoucher && t.ReversalOf == null && t.VoidedAtUtc == null
                && t.BusinessDate >= from && t.BusinessDate <= to)
            .Select(t => new { t.Id, t.ExpenseItemId, t.Amount })
            .ToListAsync(cancellationToken);
        if (invoices.Count == 0 && vouchers.Count == 0)
            return [];

        var categories = await db.PurchaseCategories.AsNoTracking().ToDictionaryAsync(c => c.Id, cancellationToken);
        var materials = await db.RawMaterials.AsNoTracking()
            .Select(m => new { m.Id, m.Name, m.CategoryId, m.PurchaseUnit, m.PurchaseUnitSize })
            .ToDictionaryAsync(m => m.Id, cancellationToken);
        var expenses = await db.ExpenseItems.AsNoTracking()
            .Select(e => new { e.Id, e.Name, e.CategoryId, e.Unit })
            .ToDictionaryAsync(e => e.Id, cancellationToken);

        var paidOut = vouchers.Select(v =>
        {
            var expense = expenses.GetValueOrDefault(v.ExpenseItemId!.Value);
            return new PostedLine(
                v.Id, PurchaseItemType.Expense, v.ExpenseItemId.Value, expense?.Name ?? "A removed expense",
                expense is null ? null : categories.GetValueOrDefault(expense.CategoryId), 1, expense?.Unit ?? "voucher", -v.Amount, IsVoucher: true);
        });

        return invoices
            .SelectMany(i => i.Lines.Select(l =>
            {
                if (l.ItemType == PurchaseItemType.Expense)
                {
                    var expense = expenses.GetValueOrDefault(l.ExpenseItemId!.Value);
                    return new PostedLine(
                        i.Id, l.ItemType, l.ExpenseItemId.Value, expense?.Name ?? "A removed expense",
                        expense is null ? null : categories.GetValueOrDefault(expense.CategoryId),
                        l.EnteredQuantity, expense?.Unit ?? l.UnitLabel, l.Cost);
                }

                var material = materials.GetValueOrDefault(l.RawMaterialId!.Value);
                return new PostedLine(
                    i.Id, l.ItemType, l.RawMaterialId.Value, material?.Name ?? "A removed material",
                    material is null ? null : categories.GetValueOrDefault(material.CategoryId),
                    material is null ? l.Quantity : l.Quantity / material.PurchaseUnitSize, material?.PurchaseUnit ?? l.UnitLabel, l.Cost);
            }))
            .Concat(paidOut)
            .ToList();
    }
}
