using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Otantik.SharedKernel.Auditing;
using Otantik.SharedKernel.Catalog;
using Otantik.SharedKernel.Customers;
using Otantik.SharedKernel.Orders;
using OtantikPos.Ordering.Application.Catalog;
using OtantikPos.Ordering.Application.Ports;

namespace OtantikPos.Node.Infrastructure.Persistence;

internal sealed class OrderStore(NodeDbContext db) : IOrderStore
{
    private IQueryable<Order> WithItems => db.Orders.Include(o => o.OrderItems).ThenInclude(i => i.AddOns);

    // The lists leave out soft-deleted orders, test and mistaken ones (Order.IsDeleted), as every
    // report in the delivery system does. One fetched by its id is still there to look at.
    private IQueryable<Order> Listed => WithItems.Where(o => !o.IsDeleted);

    public Task<Order?> GetAsync(Guid orderPublicId, CancellationToken cancellationToken) =>
        WithItems.SingleOrDefaultAsync(o => o.PublicId == orderPublicId, cancellationToken);

    // "Open" on the till's list is every order still to be paid, OrderRules.AwaitsPayment, a rule
    // both systems share: a delivery the driver has handed over stays until he brings the cash
    // back. It is applied here in memory rather than rewritten as SQL, so that there is never a
    // second copy of it to drift. The query only narrows the candidates to the handful of
    // orders still on the floor.
    public async Task<IReadOnlyList<Order>> GetOpenAsync(CancellationToken cancellationToken)
    {
        var candidates = await Listed
            .AsNoTracking()
            .AsSplitQuery()
            .Where(o => o.ClosedAt == null && o.Status != OrderStatus.Cancelled)
            .OrderBy(o => o.CreatedAt)
            .ToListAsync(cancellationToken);

        return candidates.Where(OrderRules.AwaitsPayment).ToList();
    }

    public async Task<IReadOnlyList<Order>> GetCreatedSinceAsync(DateTime sinceUtc, CancellationToken cancellationToken) =>
        await Listed
            .AsNoTracking()
            .AsSplitQuery()
            .Where(o => o.CreatedAt >= sinceUtc)
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Order>> GetCreatedBetweenAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken) =>
        await Listed
            .AsNoTracking()
            .AsSplitQuery()
            .Where(o => o.CreatedAt >= fromUtc && o.CreatedAt < toUtc)
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync(cancellationToken);

    public void Add(Order order) => db.Orders.Add(order);
}

internal sealed class CatalogStore(NodeDbContext db) : ICatalog
{
    public Task<MenuItem?> GetMenuItemAsync(int menuItemId, CancellationToken cancellationToken) =>
        db.MenuItems
            .AsNoTracking()
            .Include(m => m.Variants)
            .Include(m => m.MenuItemAddOns).ThenInclude(ma => ma.AddOn)
            .SingleOrDefaultAsync(m => m.Id == menuItemId && !m.IsDeleted, cancellationToken);

    // No tracking, so EF does not wire the lists to each other: an item's SubCategory stays
    // null and the JSON stays flat, as TillMenu promises.
    public async Task<TillMenu> GetMenuAsync(CancellationToken cancellationToken)
    {
        var categories = await db.Categories
            .AsNoTracking()
            .Where(c => !c.IsDeleted)
            .OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name)
            .ToListAsync(cancellationToken);
        var categoryIds = categories.Select(c => c.Id).ToHashSet();

        var subCategories = (await db.SubCategories
                .AsNoTracking()
                .OrderBy(s => s.DisplayOrder).ThenBy(s => s.Name)
                .ToListAsync(cancellationToken))
            .Where(s => categoryIds.Contains(s.CategoryId))
            .ToList();

        var items = await db.MenuItems
            .AsNoTracking()
            .AsSplitQuery()
            .Include(m => m.Variants.OrderBy(v => v.DisplayOrder))
            .Include(m => m.MenuItemAddOns).ThenInclude(ma => ma.AddOn)
            .Where(m => !m.IsDeleted)
            .OrderBy(m => m.Name)
            .ToListAsync(cancellationToken);

        return new TillMenu(categories, subCategories, items);
    }
}

public sealed class PricingOptions
{
    public const string Section = "Pricing";

    // Used only until the first sync with the delivery system has brought the real rate down,
    // so a freshly installed till can sell during an internet outage. Null means refuse to
    // price rather than guess.
    public decimal? TaxPercentageUntilSynced { get; set; }
}

internal sealed class PricingSettings(NodeDbContext db, IOptions<PricingOptions> options) : IPricingSettings
{
    public async Task<decimal> GetTaxPercentageAsync(CancellationToken cancellationToken)
    {
        var synced = await db.Settings.AsNoTracking()
            .Where(s => s.Id == RestaurantSettingsCopy.SingletonId)
            .Select(s => (decimal?)s.TaxPercentage)
            .SingleOrDefaultAsync(cancellationToken);

        return synced
            ?? options.Value.TaxPercentageUntilSynced
            ?? throw new DeliverySystemUnavailableException(
                "This till has not received the tax rate from the delivery system yet. Connect it to the internet once, or set Pricing:TaxPercentageUntilSynced.");
    }

    // The default until the first sync. Points are only ever redeemed online, so in practice
    // the synced rate is always there by the time it matters.
    public async Task<decimal> GetRedemptionValuePer100PointsAsync(CancellationToken cancellationToken) =>
        await db.Settings.AsNoTracking()
            .Where(s => s.Id == RestaurantSettingsCopy.SingletonId)
            .Select(s => (decimal?)s.RedemptionValuePer100Points)
            .SingleOrDefaultAsync(cancellationToken)
        ?? LoyaltyRedemption.DefaultValuePer100Points;
}

internal sealed class AuditLog(NodeDbContext db) : IAuditLog
{
    public void Add(OrderAuditEntry entry) => db.OrderAudit.Add(entry);
}
