using Microsoft.EntityFrameworkCore;
using OtantikPos.Node.Infrastructure.Persistence;

namespace OtantikPos.Node.Infrastructure.Costing;

// One sale, wherever it was rung up. DeliveryFee is after any delivery discount; BilledSubtotal
// is what the promo discount was taken from.
public sealed record SaleOrder(bool Online, decimal DiscountAmount, decimal BilledSubtotal, decimal DeliveryFee, IReadOnlyList<SaleLine> Lines);

// OnBill: still charged. A line refunded after payment was sold, and took its stock; one voided
// before payment never was.
public sealed record SaleLine(
    Guid PublicId, int MenuItemId, int? VariantId, string MenuItemName, string? VariantName, int Quantity, decimal LineTotal,
    IReadOnlyList<int> AddOnIds, bool OnBill);

// Every sale in a period for the cost reports: orders paid at the till, by when they closed, and
// online orders paid in the delivery system (OnlineSale), by when the till saw them paid. From
// inclusive, to exclusive.
public sealed class SalesLedger(NodeDbContext db)
{
    public async Task<IReadOnlyList<SaleOrder>> GetAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken)
    {
        var till = await db.Orders.AsNoTracking()
            .Where(o => !o.IsDeleted && o.ClosedAt >= fromUtc && o.ClosedAt < toUtc)
            .Include(o => o.OrderItems).ThenInclude(i => i.AddOns)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
        var online = await db.OnlineSales.AsNoTracking()
            .Where(s => s.SoldAtUtc >= fromUtc && s.SoldAtUtc < toUtc)
            .Include(s => s.Lines)
            .ToListAsync(cancellationToken);

        return
        [
            .. till.Select(o => new SaleOrder(
                false, o.DiscountAmount, o.BilledItemsSubtotal, Math.Max(o.DeliveryFee - o.DeliveryDiscountAmount, 0),
                o.OrderItems.Select(i => new SaleLine(
                    i.PublicId, i.MenuItemId, i.VariantId, i.MenuItemName, i.VariantName, i.Quantity, i.LineTotal,
                    i.AddOns.Select(a => a.AddOnId).ToList(), i.VoidType is null)).ToList())),
            .. online.Select(s => new SaleOrder(
                true, s.DiscountAmount, s.Lines.Sum(l => l.LineTotal), s.DeliveryFee,
                s.Lines.Select(l => new SaleLine(
                    l.PublicId, l.MenuItemId, l.VariantId, l.MenuItemName, l.VariantName, l.Quantity, l.LineTotal, l.AddOnIds, true)).ToList())),
        ];
    }
}
