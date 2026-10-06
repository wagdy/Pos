using System.Globalization;
using Otantik.BuildingBlocks;
using Otantik.SharedKernel.Auditing;
using Otantik.SharedKernel.Identity;
using Otantik.SharedKernel.Orders;
using OtantikPos.Ordering.Application.Catalog;
using OtantikPos.Ordering.Application.Orders;
using OtantikPos.Ordering.Application.Ports;
using OtantikPos.Ordering.Contracts;

namespace OtantikPos.Application.Tests;

public class OrderingTests
{
    private readonly TestPos _pos = new();

    private async Task<Order> TwoCheeseBurgers(OrderType type = OrderType.DineIn, string? phone = null)
    {
        var order = await _pos.Send(new OpenOrderCommand(type, TableNumber: "4", CustomerPhone: phone));
        return await _pos.Send(new AddOrderItemCommand(order.PublicId, TestPos.Burger, 2, AddOnIds: [TestPos.Cheese], Notes: "no onions"));
    }

    [Fact]
    public async Task A_captain_takes_a_dine_in_order_and_the_kitchen_gets_the_ticket()
    {
        _pos.As("captain-1", UserRole.CaptainOrder);

        var order = await TwoCheeseBurgers();
        await _pos.Send(new AddOrderItemCommand(order.PublicId, TestPos.Shawarma, VariantId: TestPos.KiloTray));
        order = await _pos.Send(new SendToKitchenCommand(order.PublicId));
        await _pos.DeliverEventsAsync();

        Assert.Equal(OrderStatus.Preparing, order.Status);
        Assert.All(order.OrderItems, i => Assert.True(i.IsSentToKitchen));
        var ticket = Assert.Single(_pos.Sink.KitchenTickets);
        Assert.Equal("Table 4", ticket.OrderLabel);
        Assert.Contains(ticket.Lines, l => l is { Name: "Burger", Quantity: 2, Notes: "no onions" } && l.AddOns.SequenceEqual(["Cheese"]));
        Assert.Contains(ticket.Lines, l => l.Name == "Shawarma (Kilo tray)");
    }

    [Fact]
    public async Task A_captain_can_neither_take_money_nor_void_anything()
    {
        _pos.As("captain-1", UserRole.CaptainOrder);
        var order = await TwoCheeseBurgers();
        var item = order.OrderItems.Single();

        Assert.Equal("You are not allowed to take payments.",
            (await Assert.ThrowsAsync<ForbiddenException>(() => _pos.Send(new CheckoutCommand(order.PublicId, PaymentMethod.Cash)))).Message);
        await Assert.ThrowsAsync<ForbiddenException>(() => _pos.Send(new VoidOrderItemCommand(order.PublicId, item.PublicId, 1, null)));
        await Assert.ThrowsAsync<ForbiddenException>(() => _pos.Send(new VoidOrderCommand(order.PublicId, "x")));
        await Assert.ThrowsAsync<ForbiddenException>(() => _pos.Send(new ApplyLoyaltyPointsCommand(order.PublicId, 10)));
        await Assert.ThrowsAsync<ForbiddenException>(() => _pos.Send(new OpenOrderCommand(OrderType.Takeaway, CustomerPhone: "0100")));

        _pos.As("cashier-1", UserRole.Cashier);
        await _pos.Send(new CheckoutCommand(order.PublicId, PaymentMethod.Cash));

        _pos.As("captain-1", UserRole.CaptainOrder);
        await Assert.ThrowsAsync<ForbiddenException>(() => _pos.Send(new PrintFinalReceiptCommand(order.PublicId)));
    }

    [Fact]
    public async Task A_captain_sees_only_their_own_tables_and_a_cashier_sees_all()
    {
        _pos.As("captain-1", UserRole.CaptainOrder);
        var first = await _pos.Send(new OpenOrderCommand(OrderType.DineIn, TableNumber: "1"));
        _pos.As("captain-2", UserRole.CaptainOrder);
        var second = await _pos.Send(new OpenOrderCommand(OrderType.DineIn, TableNumber: "2"));

        Assert.Equal([second.PublicId], (await _pos.Send(new GetOpenOrdersQuery())).Select(o => o.PublicId));
        var denied = await Assert.ThrowsAsync<ForbiddenException>(() => _pos.Send(new AddOrderItemCommand(first.PublicId, TestPos.Burger)));
        Assert.Equal("You can only work on orders you created.", denied.Message);

        _pos.As("cashier-1", UserRole.Cashier);
        Assert.Equal(2, (await _pos.Send(new GetOpenOrdersQuery())).Count);
    }

    // The menu is for taking orders: captains and cashiers read it, a driver does not.
    [Fact]
    public async Task Whoever_takes_orders_can_read_the_menu()
    {
        _pos.As("captain-1", UserRole.CaptainOrder);
        Assert.Equal(2, (await _pos.Send(new GetMenuQuery())).MenuItems.Count);
        _pos.As("cashier-1", UserRole.Cashier);
        Assert.Equal(2, (await _pos.Send(new GetMenuQuery())).MenuItems.Count);

        _pos.As("driver-1", UserRole.DeliveryCaptain);
        await Assert.ThrowsAsync<ForbiddenException>(() => _pos.Send(new GetMenuQuery()));
    }

    // Customer identification: takeaway and delivery start from the mobile number, which finds
    // the customer's profile in the delivery system.
    [Fact]
    public async Task Takeaway_asks_for_the_mobile_number_and_finds_the_customer()
    {
        _pos.Cloud.Register("customer-1", "Ali Hassan", "01001234567", 500);

        var noPhone = await Assert.ThrowsAsync<DomainException>(() => _pos.Send(new OpenOrderCommand(OrderType.Takeaway)));
        Assert.Equal("Enter the customer's mobile number.", noPhone.Message);

        var order = await _pos.Send(new OpenOrderCommand(OrderType.Takeaway, CustomerPhone: "01001234567"));
        Assert.Equal("customer-1", order.UserId);
        Assert.Equal("Ali Hassan", order.CustomerName);

        var lookup = await _pos.Send(new LookupCustomerQuery("01001234567"));
        Assert.Equal(CustomerLookupStatus.Found, lookup.Status);
        Assert.Equal(50m, lookup.PointsValue);
    }

    [Fact]
    public async Task Offline_the_order_still_opens_with_the_number_and_the_lookup_can_be_retried()
    {
        _pos.Cloud.Register("customer-1", "Ali Hassan", "01001234567", 500);
        _pos.Cloud.Offline = true;

        var order = await _pos.Send(new OpenOrderCommand(OrderType.Takeaway, CustomerPhone: "01001234567"));
        Assert.Null(order.UserId);
        Assert.Equal("01001234567", order.CustomerPhone);
        Assert.Equal(CustomerLookupStatus.Unavailable, (await _pos.Send(new AttachCustomerCommand(order.PublicId, "01001234567"))).Lookup);

        _pos.Cloud.Offline = false;
        var attached = await _pos.Send(new AttachCustomerCommand(order.PublicId, "01001234567"));
        Assert.Equal(CustomerLookupStatus.Found, attached.Lookup);
        Assert.Equal("customer-1", attached.Order.UserId);
    }

    // The delivery system's formula: (items - promo) + 14% tax, rounded to 2 places.
    [Fact]
    public async Task Totals_follow_the_delivery_systems_pricing_and_follow_voids_before_payment()
    {
        var order = await TwoCheeseBurgers();

        Assert.Equal(270m, order.BilledItemsSubtotal);
        Assert.Equal(37.80m, order.TaxAmount);
        Assert.Equal(307.80m, order.TotalAmount);

        order = await _pos.Send(new VoidOrderItemCommand(order.PublicId, order.OrderItems.Single().PublicId, 1, null));

        Assert.Equal(2, order.OrderItems.Count);
        Assert.Equal(18.90m, order.TaxAmount);
        Assert.Equal(153.90m, order.TotalAmount);
        Assert.Contains(order.OrderItems, i => i.VoidType == VoidType.BeforeKitchen);
    }

    [Fact]
    public async Task Points_come_off_after_tax_and_are_spent_in_the_delivery_system_at_checkout()
    {
        _pos.Cloud.Register("customer-1", "Ali Hassan", "01001234567", 500);
        var order = await TwoCheeseBurgers(OrderType.Takeaway, "01001234567");

        order = await _pos.Send(new ApplyLoyaltyPointsCommand(order.PublicId, 300));
        Assert.Equal(30m, order.PointsDiscountAmount);
        Assert.Equal(277.80m, order.TotalAmount);
        Assert.Equal(500, _pos.Cloud.Balances["customer-1"]);

        order = await _pos.Send(new CheckoutCommand(order.PublicId, PaymentMethod.Visa));

        Assert.Equal(200, _pos.Cloud.Balances["customer-1"]);
        Assert.Equal(("customer-1", 300), _pos.Cloud.Redemptions[order.PublicId]);
        Assert.True(OrderRules.IsSettled(order));
        Assert.Equal(PaymentStatus.Confirmed, order.PaymentStatus);
        Assert.Equal("cashier-1", order.ClosedByUserId);
        Assert.True(order.OrderItems.Single().IsSentToKitchen);
        Assert.Single(_pos.Sink.Committed<OrderSettled>());
    }

    // The payment screen shows a total, and a captain's round can land while it is open. Charging
    // the new total left the drawer short: shown L.E 3.41, charged L.E 17.07.
    [Fact]
    public async Task Payment_is_refused_when_the_bill_changed_since_the_cashier_saw_it()
    {
        var order = await TwoCheeseBurgers();
        var shown = order.TotalAmount;
        order = await _pos.Send(new AddOrderItemCommand(order.PublicId, TestPos.Burger));

        var refused = await Assert.ThrowsAsync<ConflictException>(() => _pos.Send(new CheckoutCommand(order.PublicId, PaymentMethod.Cash, shown)));
        Assert.Contains($"it is now L.E {order.TotalAmount.ToString("0.00", CultureInfo.InvariantCulture)}", refused.Message);
        Assert.Null(_pos.Orders.Saved(order.PublicId).ClosedAt);

        var paid = await _pos.Send(new CheckoutCommand(order.PublicId, PaymentMethod.Cash, order.TotalAmount));
        Assert.NotNull(paid.ClosedAt);
    }

    // For the receipt's "Cash received" and "Change". Less than the bill is refused: the screen
    // does not offer it, and a receipt with negative change would be wrong.
    [Fact]
    public async Task The_cash_handed_over_is_kept_for_the_receipt_and_must_cover_the_bill()
    {
        var order = await TwoCheeseBurgers();

        var refused = await Assert.ThrowsAsync<DomainException>(() =>
            _pos.Send(new CheckoutCommand(order.PublicId, PaymentMethod.Cash, order.TotalAmount, CashReceived: order.TotalAmount - 1)));
        Assert.Contains("less than the bill", refused.Message);
        Assert.Empty(_pos.Sink.CashReceived);

        await _pos.Send(new CheckoutCommand(order.PublicId, PaymentMethod.Cash, order.TotalAmount, CashReceived: 500));
        Assert.Equal(500m, _pos.Sink.CashReceived[order.PublicId]);
    }

    [Fact]
    public async Task When_the_points_cannot_be_spent_the_bill_stays_open()
    {
        _pos.Cloud.Register("customer-1", "Ali Hassan", "01001234567", 500);
        var order = await TwoCheeseBurgers(OrderType.Takeaway, "01001234567");
        await _pos.Send(new ApplyLoyaltyPointsCommand(order.PublicId, 300));
        _pos.Cloud.RefuseRedemption = true;

        await Assert.ThrowsAsync<ConflictException>(() => _pos.Send(new CheckoutCommand(order.PublicId, PaymentMethod.Cash)));

        var stored = _pos.Orders.Saved(order.PublicId);
        Assert.True(OrderRules.IsOpen(stored));
        Assert.Null(stored.ClosedAt);
        Assert.Empty(_pos.Sink.Committed<OrderSettled>());
    }

    [Fact]
    public async Task Points_need_the_delivery_system_to_be_reachable()
    {
        _pos.Cloud.Register("customer-1", "Ali Hassan", "01001234567", 500);
        var order = await TwoCheeseBurgers(OrderType.Takeaway, "01001234567");
        _pos.Cloud.Offline = true;

        await Assert.ThrowsAsync<DeliverySystemUnavailableException>(() => _pos.Send(new ApplyLoyaltyPointsCommand(order.PublicId, 100)));
    }

    // A walk-in has no account to take points from. OrderAccessPolicy refuses it before the
    // handler asks the cloud for a balance, so the cashier is told to look the customer up,
    // not that the delivery system is unreachable.
    [Fact]
    public async Task Points_on_a_walk_in_ask_for_the_customer_first()
    {
        // A number with no account behind it.
        var order = await TwoCheeseBurgers(OrderType.Takeaway, "01009999999");
        Assert.Null(order.UserId);
        _pos.Cloud.Offline = true;

        var refused = await Assert.ThrowsAsync<ForbiddenException>(() => _pos.Send(new ApplyLoyaltyPointsCommand(order.PublicId, 100)));
        Assert.Equal("Look the customer up by mobile number before applying points.", refused.Message);
    }

    // The rate is a system setting, the delivery system's RedemptionValuePer100Points, synced
    // down. At 20 per 100 points, an admin has made points worth double: Points / 5.
    [Fact]
    public async Task Points_follow_the_redemption_rate_set_in_the_delivery_system()
    {
        _pos.Cloud.Register("customer-1", "Ali Hassan", "01001234567", 500);
        _pos.Pricing.RedemptionValuePer100Points = 20m;

        var lookup = await _pos.Send(new LookupCustomerQuery("01001234567"));
        Assert.Equal((100m, 20m), (lookup.PointsValue, lookup.RedemptionValuePer100Points));

        var order = await TwoCheeseBurgers(OrderType.Takeaway, "01001234567");
        order = await _pos.Send(new ApplyLoyaltyPointsCommand(order.PublicId, 300));
        Assert.Equal(60m, order.PointsDiscountAmount);
        Assert.Equal(247.80m, order.TotalAmount);    // 307.80 - 60
    }

    // Void After on a customer's order: each refunded item hands back its share of the redeemed
    // points, by the same split as the money, through the outbox to the wallet in the delivery
    // system. The last item returns whatever is left, so all of them come back in the end.
    [Fact]
    public async Task A_refund_gives_the_redeemed_points_back_to_the_wallet_through_the_outbox()
    {
        _pos.Cloud.Register("customer-1", "Ali Hassan", "01001234567", 500);
        var order = await TwoCheeseBurgers(OrderType.Takeaway, "01001234567");
        await _pos.Send(new AddOrderItemCommand(order.PublicId, TestPos.Shawarma, VariantId: TestPos.KiloTray));
        await _pos.Send(new ApplyLoyaltyPointsCommand(order.PublicId, 500));
        order = await _pos.Send(new CheckoutCommand(order.PublicId, PaymentMethod.Cash));
        Assert.Equal(713.80m, order.TotalAmount);    // (270 + 400) + 14% - 50
        Assert.Equal(0, _pos.Cloud.Balances["customer-1"]);
        await _pos.DeliverEventsAsync();

        // The burgers were 270 of the 670 billed: 201 of the 500 points (floored), with 287.65
        // of the money.
        var burgers = order.OrderItems.Single(i => i.MenuItemId == TestPos.Burger);
        order = await _pos.Send(new VoidOrderItemCommand(order.PublicId, burgers.PublicId, 2, "cold"));
        var refund = Assert.Single(_pos.Sink.Committed<LoyaltyRefundDue>());
        Assert.Equal(("customer-1", 201, 287.65m), (refund.CustomerUserId, refund.PointsToReturn, refund.RefundedAmount));
        Assert.Equal(201, order.PointsRefunded);

        // The wallet changes when the outbox delivers, and only once however often it does.
        Assert.Equal(0, _pos.Cloud.Balances["customer-1"]);
        await _pos.DeliverEventsAsync();
        await _pos.RedeliverAllAsync();
        Assert.Equal(201, _pos.Cloud.Balances["customer-1"]);
        Assert.Equal(refund.EventId, Assert.Single(_pos.Cloud.Refunds).Key);

        var shawarma = order.OrderItems.Single(i => i.MenuItemId == TestPos.Shawarma);
        order = await _pos.Send(new VoidOrderItemCommand(order.PublicId, shawarma.PublicId, 1, "wrong order"));
        await _pos.DeliverEventsAsync();
        Assert.Equal((500, 713.80m), (order.PointsRefunded, order.RefundedAmount));
        Assert.Equal(500, _pos.Cloud.Balances["customer-1"]);
    }

    // A refund with no points spent still goes to the wallet: the delivery system takes back
    // what the refunded money earned. Offline, the delivery fails and the outbox keeps it.
    [Fact]
    public async Task A_full_refund_settles_the_wallet_once_the_cloud_is_back()
    {
        _pos.Cloud.Register("customer-1", "Ali Hassan", "01001234567", 500);
        var order = await TwoCheeseBurgers(OrderType.Takeaway, "01001234567");
        order = await _pos.Send(new CheckoutCommand(order.PublicId, PaymentMethod.Cash));
        await _pos.DeliverEventsAsync();

        _pos.Cloud.Offline = true;
        order = await _pos.Send(new VoidOrderCommand(order.PublicId, "complaint"));
        Assert.Equal(PaymentStatus.Refunded, order.PaymentStatus);
        var refund = Assert.Single(_pos.Sink.Committed<LoyaltyRefundDue>());
        Assert.Equal((0, 307.80m), (refund.PointsToReturn, refund.RefundedAmount));
        await Assert.ThrowsAsync<DeliverySystemUnavailableException>(() => _pos.DeliverEventsAsync());
        Assert.Empty(_pos.Cloud.Refunds);
    }

    // Nothing for the wallet without a customer account, or before anything was paid.
    [Fact]
    public async Task No_wallet_refund_for_a_walk_in_or_an_unpaid_order()
    {
        var walkIn = await TwoCheeseBurgers();
        await _pos.Send(new CheckoutCommand(walkIn.PublicId, PaymentMethod.Cash));
        await _pos.Send(new VoidOrderCommand(walkIn.PublicId, "complaint"));

        _pos.Cloud.Register("customer-1", "Ali Hassan", "01001234567", 500);
        var unpaid = await TwoCheeseBurgers(OrderType.Takeaway, "01001234567");
        await _pos.Send(new VoidOrderCommand(unpaid.PublicId, "changed their mind"));

        Assert.Empty(_pos.Sink.Committed<LoyaltyRefundDue>());
    }

    // Void After: the refund is the item's share of what was charged, and the last item gets
    // the remainder, so the refunds add up to exactly what was taken.
    [Fact]
    public async Task Void_after_payment_refunds_the_share_paid_and_is_audited()
    {
        var order = await TwoCheeseBurgers();
        order = await _pos.Send(new CheckoutCommand(order.PublicId, PaymentMethod.Cash));
        var burgers = order.OrderItems.Single();

        await Assert.ThrowsAsync<DomainException>(() => _pos.Send(new VoidOrderItemCommand(order.PublicId, burgers.PublicId, 1, " ")));
        order = await _pos.Send(new VoidOrderItemCommand(order.PublicId, burgers.PublicId, 1, "cold"));

        Assert.Equal(153.90m, order.RefundedAmount);
        Assert.Equal(PaymentStatus.PartiallyRefunded, order.PaymentStatus);
        Assert.Equal(307.80m, order.TotalAmount);
        var entry = Assert.Single(_pos.Sink.Audit, a => a.Action == OrderAuditAction.ItemVoided);
        Assert.Equal((VoidType.AfterPayment, 153.90m, "cold", "cashier-1", UserRole.Cashier), (entry.VoidType!.Value, entry.Amount, entry.Reason!, entry.UserId, entry.Role));

        order = await _pos.Send(new VoidOrderCommand(order.PublicId, "customer complaint"));

        Assert.Equal(307.80m, order.RefundedAmount);
        Assert.Equal(PaymentStatus.Refunded, order.PaymentStatus);
    }

    [Fact]
    public async Task A_final_receipt_prints_only_for_a_paid_order()
    {
        var order = await TwoCheeseBurgers();
        await Assert.ThrowsAsync<ForbiddenException>(() => _pos.Send(new PrintFinalReceiptCommand(order.PublicId)));

        await _pos.Send(new CheckoutCommand(order.PublicId, PaymentMethod.Cash));
        await _pos.Send(new PrintFinalReceiptCommand(order.PublicId));

        Assert.Equal([order.PublicId], _pos.Sink.Receipts);
        Assert.Contains(_pos.Sink.Audit, a => a.Action == OrderAuditAction.FinalReceiptPrinted);
    }

    [Fact]
    public async Task Every_change_is_queued_for_the_cloud_and_pushed_to_the_tills()
    {
        var order = await TwoCheeseBurgers();
        await _pos.Send(new SendToKitchenCommand(order.PublicId));

        Assert.Equal(3, _pos.Sink.Committed<OrderChanged>().Count());
        Assert.Equal(3, _pos.Sink.TillPushes);
    }
}
