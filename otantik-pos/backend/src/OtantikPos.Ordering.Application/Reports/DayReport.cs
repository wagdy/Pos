using MediatR;
using Otantik.BuildingBlocks;
using Otantik.SharedKernel.Authorization;
using Otantik.SharedKernel.Orders;
using OtantikPos.Ordering.Application.Ports;

namespace OtantikPos.Ordering.Application.Reports;

// The till's report for one business day: every order opened in it, and what they came to.
// Read from the till's own database, so it is there with or without the internet.
//
// A day's orders are the ones opened in it. Money is counted from the orders that were paid:
// what they charged, what was refunded on them, and by which payment method. Loyalty points are
// shown on their own, as the discount they gave; they are not money taken.
public sealed record GetDayReportQuery(DateOnly? BusinessDate = null) : IRequest<DayReport>;

public sealed record DayReport(
    DateOnly BusinessDate,
    DateTime FromUtc,
    DateTime ToUtc,
    DaySummary Summary,
    IReadOnlyList<PaymentMethodTotal> ByPaymentMethod,
    IReadOnlyList<OrderTypeTotal> ByOrderType,
    IReadOnlyList<Order> Orders);

public sealed record DaySummary(
    int Orders,
    int PaidOrders,
    int OpenOrders,
    decimal OpenValue,
    int CancelledOrders,
    // What the paid orders charged, after any points discount.
    decimal GrossSales,
    decimal Refunds,
    decimal NetSales,
    decimal AverageTicket,
    decimal TaxCharged,
    int PointsRedeemed,
    decimal PointsDiscount,
    int PointsReturned,
    // Voided before payment. Before the kitchen it simply came off the bill; after it, the food
    // was made and is written off.
    VoidTotal VoidedBeforeKitchen,
    VoidTotal VoidedAfterKitchen);

public sealed record VoidTotal(int Items, decimal Value);

public sealed record PaymentMethodTotal(PaymentMethod Method, int Orders, decimal Charged, decimal Refunded, decimal Net);

public sealed record OrderTypeTotal(OrderType Type, int Orders, decimal Net);

internal sealed class GetDayReportHandler(IOrderStore orders, IBusinessCalendar calendar, ICurrentUser user, TimeProvider time)
    : IRequestHandler<GetDayReportQuery, DayReport>
{
    public async Task<DayReport> Handle(GetDayReportQuery request, CancellationToken cancellationToken)
    {
        if (!user.Has(Permissions.OrderViewAll))
            throw new ForbiddenException("You are not allowed to see the day's takings.");

        var day = request.BusinessDate ?? calendar.BusinessDateOf(time.GetUtcNow().UtcDateTime);
        var (from, to) = calendar.Bounds(day);
        return DayReports.Build(day, from, to, await orders.GetCreatedBetweenAsync(from, to, cancellationToken));
    }
}

public static class DayReports
{
    public static DayReport Build(DateOnly day, DateTime fromUtc, DateTime toUtc, IReadOnlyList<Order> orders)
    {
        var paid = orders.Where(OrderRules.IsSettled).ToList();
        var gross = paid.Sum(o => o.TotalAmount);
        var refunds = paid.Sum(o => o.RefundedAmount);
        // Still to be paid, including a delivery handed over before its cash came back.
        var open = orders.Where(OrderRules.AwaitsPayment).ToList();
        var items = orders.SelectMany(o => o.OrderItems).ToList();

        var summary = new DaySummary(
            Orders: orders.Count,
            PaidOrders: paid.Count,
            OpenOrders: open.Count,
            OpenValue: open.Sum(o => o.TotalAmount),
            CancelledOrders: orders.Count(o => o.Status == OrderStatus.Cancelled),
            GrossSales: gross,
            Refunds: refunds,
            NetSales: gross - refunds,
            AverageTicket: paid.Count == 0 ? 0 : Math.Round(gross / paid.Count, 2, MidpointRounding.AwayFromZero),
            TaxCharged: paid.Sum(o => o.TaxAmount),
            PointsRedeemed: paid.Sum(o => o.PointsRedeemed),
            PointsDiscount: paid.Sum(o => o.PointsDiscountAmount),
            PointsReturned: paid.Sum(o => o.PointsRefunded),
            VoidedBeforeKitchen: VoidsOf(items, VoidType.BeforeKitchen),
            VoidedAfterKitchen: VoidsOf(items, VoidType.AfterKitchen));

        var byMethod = paid
            .GroupBy(o => o.PaymentMethod)
            .OrderBy(g => g.Key)
            .Select(g => new PaymentMethodTotal(
                g.Key, g.Count(), g.Sum(o => o.TotalAmount), g.Sum(o => o.RefundedAmount), g.Sum(o => o.TotalAmount - o.RefundedAmount)))
            .ToList();

        var byType = paid
            .GroupBy(o => o.Type)
            .OrderBy(g => g.Key)
            .Select(g => new OrderTypeTotal(g.Key, g.Count(), g.Sum(o => o.TotalAmount - o.RefundedAmount)))
            .ToList();

        return new DayReport(day, fromUtc, toUtc, summary, byMethod, byType, orders);
    }

    private static VoidTotal VoidsOf(IEnumerable<OrderItem> items, VoidType type)
    {
        var voided = items.Where(i => i.VoidType == type).ToList();
        return new VoidTotal(voided.Sum(i => i.Quantity), voided.Sum(i => i.LineTotal));
    }
}
