using Otantik.BuildingBlocks;
using Otantik.SharedKernel.Identity;
using Otantik.SharedKernel.Orders;
using OtantikPos.Ordering.Application.Orders;
using OtantikPos.Ordering.Application.Reports;

namespace OtantikPos.Application.Tests;

public class ReportTests
{
    private readonly TestPos _pos = new();

    // A day with a bit of everything: a cash takeaway with points and a partial refund, a card
    // dine-in, a table still open, an order cancelled after the kitchen had it, and a line voided
    // before it did.
    [Fact]
    public async Task The_day_report_counts_what_was_taken_refunded_and_written_off()
    {
        _pos.Cloud.Register("customer-1", "Ali Hassan", "01001234567", 500);

        // 2 x (120 + 15) + 14% = 307.80, less 100 points (10.00): 297.80 in cash. One burger
        // refunded later: its share, 148.90, and 50 of the points.
        var takeaway = await _pos.Send(new OpenOrderCommand(OrderType.Takeaway, CustomerPhone: "01001234567"));
        await _pos.Send(new AddOrderItemCommand(takeaway.PublicId, TestPos.Burger, 2, AddOnIds: [TestPos.Cheese]));
        await _pos.Send(new ApplyLoyaltyPointsCommand(takeaway.PublicId, 100));
        takeaway = await _pos.Send(new CheckoutCommand(takeaway.PublicId, PaymentMethod.Cash));
        await _pos.Send(new VoidOrderItemCommand(takeaway.PublicId, takeaway.OrderItems.Single().PublicId, 1, "cold"));

        // 400 + 14% = 456.00 by card.
        var table1 = await _pos.Send(new OpenOrderCommand(OrderType.DineIn, TableNumber: "1"));
        await _pos.Send(new AddOrderItemCommand(table1.PublicId, TestPos.Shawarma, VariantId: TestPos.KiloTray));
        await _pos.Send(new CheckoutCommand(table1.PublicId, PaymentMethod.Visa));

        // Still open at 136.80, after a shawarma came off before the kitchen saw it.
        var table3 = await _pos.Send(new OpenOrderCommand(OrderType.DineIn, TableNumber: "3"));
        await _pos.Send(new AddOrderItemCommand(table3.PublicId, TestPos.Burger));
        table3 = await _pos.Send(new AddOrderItemCommand(table3.PublicId, TestPos.Shawarma, VariantId: TestPos.KiloTray));
        await _pos.Send(new VoidOrderItemCommand(table3.PublicId, table3.OrderItems.Single(i => i.MenuItemId == TestPos.Shawarma).PublicId, 1, null));

        // Cancelled once the kitchen had made it: a burger written off.
        var table4 = await _pos.Send(new OpenOrderCommand(OrderType.DineIn, TableNumber: "4"));
        await _pos.Send(new AddOrderItemCommand(table4.PublicId, TestPos.Burger));
        await _pos.Send(new SendToKitchenCommand(table4.PublicId));
        await _pos.Send(new VoidOrderCommand(table4.PublicId, "left"));

        var report = await _pos.Send(new GetDayReportQuery());
        var summary = report.Summary;

        Assert.Equal(TestCalendar.Today, report.BusinessDate);
        Assert.Equal(4, report.Orders.Count);
        Assert.Equal((4, 2, 1, 136.80m, 1), (summary.Orders, summary.PaidOrders, summary.OpenOrders, summary.OpenValue, summary.CancelledOrders));
        Assert.Equal((753.80m, 148.90m, 604.90m, 376.90m), (summary.GrossSales, summary.Refunds, summary.NetSales, summary.AverageTicket));
        Assert.Equal(93.80m, summary.TaxCharged);
        Assert.Equal((100, 10.00m, 50), (summary.PointsRedeemed, summary.PointsDiscount, summary.PointsReturned));
        Assert.Equal(new VoidTotal(1, 400m), summary.VoidedBeforeKitchen);
        Assert.Equal(new VoidTotal(1, 120m), summary.VoidedAfterKitchen);

        Assert.Equal(
            [new PaymentMethodTotal(PaymentMethod.Cash, 1, 297.80m, 148.90m, 148.90m), new PaymentMethodTotal(PaymentMethod.Visa, 1, 456.00m, 0m, 456.00m)],
            report.ByPaymentMethod);
        Assert.Equal([new OrderTypeTotal(OrderType.Takeaway, 1, 148.90m), new OrderTypeTotal(OrderType.DineIn, 1, 456.00m)], report.ByOrderType);
    }

    [Fact]
    public async Task Another_day_has_its_own_orders()
    {
        await _pos.Send(new OpenOrderCommand(OrderType.DineIn, TableNumber: "1"));

        var yesterday = await _pos.Send(new GetDayReportQuery(TestCalendar.Today.AddDays(-1)));

        Assert.Empty(yesterday.Orders);
        Assert.Equal(0, yesterday.Summary.Orders);
    }

    // The takings are for the people who take them.
    [Fact]
    public async Task A_captain_cannot_see_the_takings()
    {
        _pos.As("captain-1", UserRole.CaptainOrder);

        var refused = await Assert.ThrowsAsync<ForbiddenException>(() => _pos.Send(new GetDayReportQuery()));
        Assert.Equal("You are not allowed to see the day's takings.", refused.Message);
    }
}
