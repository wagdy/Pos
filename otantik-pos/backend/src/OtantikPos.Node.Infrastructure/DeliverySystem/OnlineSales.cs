using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Otantik.SharedKernel.Orders;
using OtantikPos.Node.Infrastructure.Common;
using OtantikPos.Node.Infrastructure.Persistence;
using OtantikPos.Ordering.Application.Ports;
using OtantikPos.Ordering.Contracts;

namespace OtantikPos.Node.Infrastructure.DeliverySystem;

// An online order, placed and paid in the delivery system and run from its screens, as the till
// records it: what was sold and when, for stock and the cost reports. Never an order on the till:
// it is not in the open orders, the day's takings or the cash drawer, which are the till's own.
public sealed class OnlineSale
{
    public Guid PublicId { get; init; }

    // When the delivery system last changed the order, as the till first saw it paid: the moment
    // of payment for Instapay, of handing it over for cash on delivery.
    public DateTime SoldAtUtc { get; init; }

    public DateTime ReceivedAtUtc { get; init; }
    public OrderType Type { get; init; }
    public PaymentMethod PaymentMethod { get; init; }

    // The order's promo discount, shared over its lines by value in the reports, and its delivery
    // fee after any delivery discount. Before VAT, as the till's own figures are.
    public decimal DiscountAmount { get; init; }
    public decimal DeliveryFee { get; init; }

    public List<OnlineSaleLine> Lines { get; init; } = [];
}

// A line still on the bill when it was paid. Its PublicId is the stock movements' SourceId, as
// a till order line's is.
public sealed class OnlineSaleLine
{
    public Guid PublicId { get; init; }
    public Guid OnlineSalePublicId { get; init; }
    public int MenuItemId { get; init; }
    public int? VariantId { get; init; }
    public string MenuItemName { get; init; } = string.Empty;
    public string? VariantName { get; init; }
    public int Quantity { get; init; }

    // Quantity × (unit price + add-ons), as OrderItem.LineTotal.
    public decimal LineTotal { get; init; }

    public List<int> AddOnIds { get; init; } = [];
}

internal sealed class OnlineSaleConfiguration : IEntityTypeConfiguration<OnlineSale>
{
    public void Configure(EntityTypeBuilder<OnlineSale> builder)
    {
        builder.ToTable("OnlineSales", NodeDbContext.PosSchema);
        builder.HasKey(s => s.PublicId);
        builder.Property(s => s.Type).HasConversion<string>().HasMaxLength(32);
        builder.Property(s => s.PaymentMethod).HasConversion<string>().HasMaxLength(32);
        builder.HasMany(s => s.Lines).WithOne().HasForeignKey(l => l.OnlineSalePublicId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(s => s.SoldAtUtc);
    }
}

internal sealed class OnlineSaleLineConfiguration : IEntityTypeConfiguration<OnlineSaleLine>
{
    public void Configure(EntityTypeBuilder<OnlineSaleLine> builder)
    {
        builder.ToTable("OnlineSaleLines", NodeDbContext.PosSchema);
        builder.HasKey(l => l.PublicId);
        builder.Property(l => l.MenuItemName).HasMaxLength(200);
        builder.Property(l => l.VariantName).HasMaxLength(200);
    }
}

// Records each online order the first time it is seen paid, and takes its stock: an OrderSettled
// in the outbox, saved with it, as a payment at the till does. Seen again, it changes nothing;
// one cancelled or refunded before it was paid here is not a sale. A refund after that is not
// taken back: the food was made.
public sealed class OnlineSalesImporter(NodeDbContext db, IEventOutbox outbox)
{
    public async Task<int> ImportAsync(IReadOnlyList<Order> orders, CancellationToken cancellationToken)
    {
        var ids = orders.Select(o => o.PublicId).ToList();
        var known = (await db.OnlineSales.Where(s => ids.Contains(s.PublicId)).Select(s => s.PublicId).ToListAsync(cancellationToken)).ToHashSet();

        var imported = 0;
        foreach (var order in orders)
        {
            var live = order.OrderItems.Where(i => !i.IsVoided).ToList();
            if (known.Contains(order.PublicId)
                || !OrderRules.IsSettled(order)
                || order.Status == OrderStatus.Cancelled
                || order.PaymentStatus == PaymentStatus.Refunded
                || live.Count == 0)
                continue;

            db.OnlineSales.Add(new OnlineSale
            {
                PublicId = order.PublicId,
                SoldAtUtc = DateTime.SpecifyKind(order.UpdatedAt, DateTimeKind.Utc),
                ReceivedAtUtc = DateTime.UtcNow,
                Type = order.Type,
                PaymentMethod = order.PaymentMethod,
                DiscountAmount = order.DiscountAmount,
                DeliveryFee = Math.Max(order.DeliveryFee - order.DeliveryDiscountAmount, 0),
                Lines = live.Select(i => new OnlineSaleLine
                {
                    PublicId = i.PublicId,
                    OnlineSalePublicId = order.PublicId,
                    MenuItemId = i.MenuItemId,
                    VariantId = i.VariantId,
                    MenuItemName = i.MenuItemName,
                    VariantName = i.VariantName,
                    Quantity = i.Quantity,
                    LineTotal = i.LineTotal,
                    AddOnIds = i.AddOns.Select(a => a.AddOnId).ToList(),
                }).ToList(),
            });
            outbox.Add(new OrderSettled(
                order.PublicId,
                live.Select(i => new SoldLine(i.PublicId, i.MenuItemId, i.VariantId, i.AddOns.Select(a => a.AddOnId).ToList(), i.Quantity)).ToList()));
            known.Add(order.PublicId);
            imported++;
        }

        if (imported > 0)
            await db.SaveChangesAsync(cancellationToken);
        return imported;
    }
}

// Reads the delivery system's online orders every few minutes, and at once when it comes back,
// from where it left off. Started for the first time, it begins from then: online orders before
// the till ran are in no stock count of its own.
public sealed class OnlineSalesSync(
    IServiceScopeFactory scopeFactory,
    IOptions<DeliverySystemOptions> options,
    DeliverySystemLink link,
    ILogger<OnlineSalesSync> logger) : BackgroundService
{
    // The delivery system stamps an order before it commits it, so one stamped a moment before
    // the last one read can still arrive; asking again from a little earlier catches it.
    private static readonly TimeSpan Overlap = TimeSpan.FromMinutes(2);

    private readonly SyncSignal _syncNow = new();
    private DeliverySystemLinkState _lastLinkState = link.Current.State;
    private bool _toldNotOffered;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.IsConfigured)
            return;

        var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.ReferenceDataRefreshMinutes));
        link.Changed += OnLinkChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await SyncAsync(stoppingToken);
                await _syncNow.WaitAsync(interval, stoppingToken);
            }
        }
        finally
        {
            link.Changed -= OnLinkChanged;
        }
    }

    // One pass: what changed since the cursor, imported, and the cursor moved on.
    public async Task SyncAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NodeDbContext>();
            var cursor = await db.SyncState.SingleOrDefaultAsync(s => s.Key == SyncState.OnlineOrdersChangedSince, stoppingToken);
            if (cursor is null)
            {
                db.SyncState.Add(cursor = new SyncState { Key = SyncState.OnlineOrdersChangedSince, Value = Stamp(DateTime.UtcNow) });
                await db.SaveChangesAsync(stoppingToken);
            }

            var since = DateTime.Parse(cursor.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var orders = await scope.ServiceProvider.GetRequiredService<DeliverySystemApi>().GetOnlineOrdersChangedSinceAsync(since, stoppingToken);
            if (orders is null)
            {
                // The cursor moves on with time: once the delivery system is updated, its online
                // orders take stock from then, not all the weeks before at once, which the stock
                // counts since have already found missing.
                cursor.Value = Stamp(DateTime.UtcNow - Overlap);
                await db.SaveChangesAsync(stoppingToken);
                if (!_toldNotOffered)
                    logger.LogWarning("The delivery system does not offer its online orders yet: they take no stock here until it is updated");
                _toldNotOffered = true;
                return;
            }
            _toldNotOffered = false;

            var imported = await scope.ServiceProvider.GetRequiredService<OnlineSalesImporter>().ImportAsync(orders, stoppingToken);
            if (imported > 0)
                logger.LogInformation("Took the stock of {Count} online orders paid in the delivery system", imported);

            if (orders.Count > 0)
            {
                var latest = DateTime.SpecifyKind(orders.Max(o => o.UpdatedAt), DateTimeKind.Utc) - Overlap;
                if (latest > since)
                {
                    cursor.Value = Stamp(latest);
                    await db.SaveChangesAsync(stoppingToken);
                }
            }
        }
        catch (DeliverySystemUnavailableException) when (!stoppingToken.IsCancellationRequested)
        {
            // Offline: the cursor stays, and the next pass catches up.
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Reading online orders from the delivery system failed");
        }
    }

    private void OnLinkChanged(DeliverySystemLinkStatus status)
    {
        var previous = _lastLinkState;
        _lastLinkState = status.State;
        if (status.State == DeliverySystemLinkState.Online && previous == DeliverySystemLinkState.Offline)
            _syncNow.Notify();
    }

    private static string Stamp(DateTime utc) => utc.ToString("O", CultureInfo.InvariantCulture);

    private sealed class SyncSignal : WakeSignal;
}
