using System.Text.Json;
using Otantik.SharedKernel.Catalog;
using Otantik.SharedKernel.Customers;
using Otantik.SharedKernel.Identity;
using Otantik.SharedKernel.Orders;

namespace Otantik.Domain.Tests;

public class SharedModelTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Void_type_follows_the_items_and_orders_state()
    {
        var notSent = Orders.Item(50);
        var sent = Orders.Item(100, sent: true);
        var order = Orders.DineIn("captain", notSent, sent);

        Assert.Equal(VoidType.BeforeKitchen, OrderRules.VoidTypeFor(order, notSent));
        Assert.Equal(VoidType.AfterKitchen, OrderRules.VoidTypeFor(order, sent));

        order.Closed("cashier");
        Assert.Equal(VoidType.AfterPayment, OrderRules.VoidTypeFor(order, sent));

        sent.VoidType = VoidType.AfterPayment;
        Assert.Null(OrderRules.VoidTypeFor(order, sent));
    }

    // The delivery system marks a cash-on-delivery order Confirmed when it is placed. Until it
    // is delivered, nobody has paid.
    [Fact]
    public void Cash_on_delivery_is_not_settled_until_delivered()
    {
        var order = new Order { Type = OrderType.Delivery, PaymentMethod = PaymentMethod.Cash, PaymentStatus = PaymentStatus.Confirmed, Status = OrderStatus.Preparing };
        Assert.False(OrderRules.IsSettled(order));

        order.Status = OrderStatus.Delivered;
        Assert.True(OrderRules.IsSettled(order));

        var visa = new Order { PaymentMethod = PaymentMethod.Visa, PaymentStatus = PaymentStatus.Confirmed, Status = OrderStatus.Preparing };
        Assert.True(OrderRules.IsSettled(visa));
    }

    [Fact]
    public void Served_is_a_fulfilled_status_like_delivered_and_collected()
    {
        Assert.True(OrderStatuses.IsFulfilled(OrderStatus.Served));
        Assert.True(OrderStatuses.IsFinal(OrderStatus.Served));
    }

    [Fact]
    public void Customer_phone_is_required_for_takeaway_and_delivery_only()
    {
        Assert.True(OrderRules.RequiresCustomerPhone(OrderType.Takeaway));
        Assert.True(OrderRules.RequiresCustomerPhone(OrderType.Delivery));
        Assert.False(OrderRules.RequiresCustomerPhone(OrderType.DineIn));
    }

    [Fact]
    public void Discount_is_points_divided_by_ten_at_the_default_rate()
    {
        const decimal rate = LoyaltyRedemption.DefaultValuePer100Points;
        Assert.Equal(1.5m, LoyaltyRedemption.DiscountFor(15, rate));
        Assert.Equal(40m, LoyaltyRedemption.DiscountFor(400, rate));
        Assert.Equal(0m, LoyaltyRedemption.DiscountFor(-5, rate));
        Assert.Equal(555, LoyaltyRedemption.MaxPointsFor(55.55m, rate));
    }

    // The rate is the delivery system's RedemptionValuePer100Points, and the arithmetic its
    // LoyaltyPointsValue's, so both systems agree to the piastre at any setting.
    [Fact]
    public void Another_rate_prices_points_exactly_as_the_delivery_system_does()
    {
        Assert.Equal(80m, LoyaltyRedemption.DiscountFor(400, 20m));     // Points / 5
        Assert.Equal(4.13m, LoyaltyRedemption.DiscountFor(33, 12.5m));  // 4.125, half away from zero
        Assert.Equal(444, LoyaltyRedemption.MaxPointsFor(55.55m, 12.5m));
        Assert.Equal(0m, LoyaltyRedemption.DiscountFor(400, 0m));       // redemption switched off
        Assert.Equal(0, LoyaltyRedemption.MaxPointsFor(55.55m, 0m));
    }

    [Fact]
    public void IsPickup_still_answers_for_existing_readers()
    {
        Assert.True(new Order { Type = OrderType.Takeaway }.IsPickup);
        Assert.False(new Order { Type = OrderType.DineIn }.IsPickup);
        Assert.False(new Order().IsPickup);
    }

    [Fact]
    public void Line_total_matches_the_delivery_systems_receipt_arithmetic()
    {
        var item = Orders.Item(100, quantity: 2);
        item.AddOns.Add(new OrderItemAddOn { Name = "Cheese", Price = 15 });

        // 2 × (100 + 15), as OrderItemResponse.LineTotal computes it.
        Assert.Equal(230m, item.LineTotal);
    }

    [Fact]
    public void Items_voided_before_payment_leave_the_bill_but_refunded_ones_stay()
    {
        var kept = Orders.Item(100);
        var voidedBefore = Orders.Item(50);
        voidedBefore.VoidType = VoidType.BeforeKitchen;
        var refunded = Orders.Item(30);
        refunded.VoidType = VoidType.AfterPayment;
        var order = Orders.DineIn("captain", kept, voidedBefore, refunded);

        Assert.Equal(130m, order.BilledItemsSubtotal);
    }

    // The order itself is the sync payload between the cloud and the restaurant machine: it
    // must survive a round trip, carry its identities, and never leak the account behind it.
    [Fact]
    public void An_order_travels_as_json_without_its_user_or_catalog_graph()
    {
        var item = Orders.Item(120, quantity: 2);
        item.Notes = "no onions";
        item.MenuItem = new MenuItem { Id = 1, Name = "Burger" };
        item.AddOns.Add(new OrderItemAddOn { AddOnId = 7, Name = "Cheese", Price = 15, OrderItem = item, AddOn = new AddOn { Id = 7 } });
        var order = Orders.DineIn("captain-1", item);
        order.User = new AppUser { Id = "customer-1", PasswordHash = "secret-hash" };
        order.UserId = "customer-1";

        var json = JsonSerializer.Serialize(order, Web);
        var back = JsonSerializer.Deserialize<Order>(json, Web)!;

        Assert.DoesNotContain("secret-hash", json);
        Assert.DoesNotContain("\"user\"", json);
        Assert.DoesNotContain("\"menuItem\"", json);
        Assert.DoesNotContain("\"isPickup\"", json);
        Assert.Equal(order.PublicId, back.PublicId);
        Assert.Equal(OrderType.DineIn, back.Type);
        Assert.Equal("T4", back.TableNumber);
        var backItem = Assert.Single(back.OrderItems);
        Assert.Equal(item.PublicId, backItem.PublicId);
        Assert.Equal("no onions", backItem.Notes);
        Assert.Equal(270m, backItem.LineTotal);
    }
}
