using Otantik.SharedKernel.Authorization;
using Otantik.SharedKernel.Identity;
using Otantik.SharedKernel.Orders;

namespace Otantik.Domain.Tests;

// The Captain Order restrictions, as stated: create and submit orders, and nothing else. No
// checkout, no final receipt, no void of any kind. Cashier and Manager: full authority.
public class OrderAccessPolicyTests
{
    private static readonly TestUser Captain = new("captain-1", UserRole.CaptainOrder);
    private static readonly TestUser OtherCaptain = new("captain-2", UserRole.CaptainOrder);
    private static readonly TestUser Cashier = new("cashier-1", UserRole.Cashier);
    private static readonly TestUser Driver = new("driver-1", UserRole.DeliveryCaptain);

    [Fact]
    public void Captain_creates_dine_in_orders_only()
    {
        Assert.True(OrderAccessPolicy.CanCreate(Captain, OrderType.DineIn).IsAllowed);
        Assert.False(OrderAccessPolicy.CanCreate(Captain, OrderType.Takeaway).IsAllowed);
        Assert.False(OrderAccessPolicy.CanCreate(Captain, OrderType.Delivery).IsAllowed);
    }

    [Fact]
    public void Captain_adds_and_submits_on_their_own_open_order()
    {
        var order = Orders.DineIn(Captain.UserId, Orders.Item(100));

        Assert.True(OrderAccessPolicy.Evaluate(Captain, OrderAction.AddItems, order).IsAllowed);
        Assert.True(OrderAccessPolicy.Evaluate(Captain, OrderAction.SendToKitchen, order).IsAllowed);
    }

    [Fact]
    public void Captain_cannot_touch_another_captains_order()
    {
        var order = Orders.DineIn(OtherCaptain.UserId, Orders.Item(100));

        var decision = OrderAccessPolicy.Evaluate(Captain, OrderAction.AddItems, order);

        Assert.False(decision.IsAllowed);
        Assert.Equal("You can only work on orders you created.", decision.Reason);
    }

    [Fact]
    public void Captain_cannot_add_to_a_closed_order()
    {
        var order = Orders.DineIn(Captain.UserId, Orders.Item(100)).Closed(Cashier.UserId);

        Assert.False(OrderAccessPolicy.Evaluate(Captain, OrderAction.AddItems, order).IsAllowed);
    }

    [Fact]
    public void Captain_cannot_take_payment_print_a_final_receipt_or_apply_points()
    {
        var open = Orders.DineIn(Captain.UserId, Orders.Item(100));
        open.UserId = "customer-1";
        var paid = Orders.DineIn(Captain.UserId, Orders.Item(100)).Closed(Cashier.UserId);

        Assert.False(OrderAccessPolicy.Evaluate(Captain, OrderAction.Checkout, open).IsAllowed);
        Assert.False(OrderAccessPolicy.Evaluate(Captain, OrderAction.ApplyLoyalty, open).IsAllowed);
        Assert.False(OrderAccessPolicy.Evaluate(Captain, OrderAction.PrintFinalReceipt, paid).IsAllowed);
        Assert.False(OrderAccessPolicy.CanChangeStatus(Captain, open, OrderStatus.Served).IsAllowed);
    }

    // Void Before, after the kitchen, Void After, the whole order, and cancelling through a
    // status change: every route is closed.
    [Fact]
    public void Captain_cannot_void_by_any_route()
    {
        var notSent = Orders.Item(50);
        var sent = Orders.Item(100, sent: true);
        var open = Orders.DineIn(Captain.UserId, notSent, sent);
        var paidItem = Orders.Item(100, sent: true);
        var paid = Orders.DineIn(Captain.UserId, paidItem).Closed(Cashier.UserId);

        Assert.False(OrderAccessPolicy.Evaluate(Captain, OrderAction.VoidItem, open, notSent).IsAllowed);
        Assert.False(OrderAccessPolicy.Evaluate(Captain, OrderAction.VoidItem, open, sent).IsAllowed);
        Assert.False(OrderAccessPolicy.Evaluate(Captain, OrderAction.VoidItem, paid, paidItem).IsAllowed);
        Assert.False(OrderAccessPolicy.Evaluate(Captain, OrderAction.VoidOrder, open).IsAllowed);
        Assert.False(OrderAccessPolicy.CanChangeStatus(Captain, open, OrderStatus.Cancelled).IsAllowed);
    }

    [Theory]
    [InlineData(UserRole.Cashier)]
    [InlineData(UserRole.Manager)]
    [InlineData(UserRole.Admin)]
    public void Cashier_and_manager_have_full_authority(UserRole role)
    {
        var user = new TestUser("staff-1", role);
        var notSent = Orders.Item(50);
        var sent = Orders.Item(100, sent: true);
        var open = Orders.DineIn(Captain.UserId, notSent, sent);
        open.UserId = "customer-1";

        Assert.True(OrderAccessPolicy.Evaluate(user, OrderAction.AddItems, open).IsAllowed);
        Assert.True(OrderAccessPolicy.Evaluate(user, OrderAction.ApplyLoyalty, open).IsAllowed);
        Assert.True(OrderAccessPolicy.Evaluate(user, OrderAction.VoidItem, open, notSent).IsAllowed);
        Assert.True(OrderAccessPolicy.Evaluate(user, OrderAction.VoidItem, open, sent).IsAllowed);
        Assert.True(OrderAccessPolicy.Evaluate(user, OrderAction.Checkout, open).IsAllowed);

        var paidItem = Orders.Item(100, sent: true);
        var paid = Orders.DineIn(Captain.UserId, paidItem).Closed(user.UserId);
        Assert.True(OrderAccessPolicy.Evaluate(user, OrderAction.PrintFinalReceipt, paid).IsAllowed);
        Assert.True(OrderAccessPolicy.Evaluate(user, OrderAction.VoidItem, paid, paidItem).IsAllowed);
        Assert.True(OrderAccessPolicy.Evaluate(user, OrderAction.VoidOrder, paid).IsAllowed);
    }

    [Fact]
    public void A_final_receipt_needs_a_paid_order_even_for_a_cashier()
    {
        var open = Orders.DineIn(Captain.UserId, Orders.Item(100));

        Assert.False(OrderAccessPolicy.Evaluate(Cashier, OrderAction.PrintFinalReceipt, open).IsAllowed);
    }

    [Fact]
    public void Takeaway_cannot_be_paid_without_the_customers_mobile_number()
    {
        var order = Orders.DineIn(Cashier.UserId, Orders.Item(100));
        order.Type = OrderType.Takeaway;

        Assert.Equal("Enter the customer's mobile number first.", OrderAccessPolicy.Evaluate(Cashier, OrderAction.Checkout, order).Reason);

        order.CustomerPhone = "01001234567";
        Assert.True(OrderAccessPolicy.Evaluate(Cashier, OrderAction.Checkout, order).IsAllowed);
    }

    // Cash on delivery: the driver hands the food over, often marking it delivered from the
    // delivery app, and brings the money back after. The till must still take it.
    [Fact]
    public void A_delivered_order_can_still_be_paid_but_not_added_to()
    {
        var order = Orders.DineIn(Cashier.UserId, Orders.Item(100, sent: true));
        order.Type = OrderType.Delivery;
        order.CustomerPhone = "01001234567";
        order.Status = OrderStatus.Delivered;

        Assert.True(OrderAccessPolicy.Evaluate(Cashier, OrderAction.Checkout, order).IsAllowed);
        Assert.False(OrderAccessPolicy.Evaluate(Cashier, OrderAction.AddItems, order).IsAllowed);
    }

    [Fact]
    public void A_paid_or_cancelled_order_cannot_be_paid_again()
    {
        var paid = Orders.DineIn(Cashier.UserId, Orders.Item(100)).Closed(Cashier.UserId);
        var cancelled = Orders.DineIn(Cashier.UserId, Orders.Item(100));
        cancelled.Status = OrderStatus.Cancelled;

        Assert.Equal("This order is already paid or cancelled.", OrderAccessPolicy.Evaluate(Cashier, OrderAction.Checkout, paid).Reason);
        Assert.False(OrderAccessPolicy.Evaluate(Cashier, OrderAction.Checkout, cancelled).IsAllowed);
    }

    [Fact]
    public void Points_need_a_customer_profile_attached()
    {
        var order = Orders.DineIn(Cashier.UserId, Orders.Item(100));

        Assert.False(OrderAccessPolicy.Evaluate(Cashier, OrderAction.ApplyLoyalty, order).IsAllowed);
    }

    [Fact]
    public void A_driver_moves_deliveries_along_but_cannot_cancel_them()
    {
        var order = Orders.DineIn("someone", Orders.Item(100));
        order.Type = OrderType.Delivery;

        Assert.True(OrderAccessPolicy.CanChangeStatus(Driver, order, OrderStatus.OutForDelivery).IsAllowed);
        Assert.False(OrderAccessPolicy.CanChangeStatus(Driver, order, OrderStatus.Cancelled).IsAllowed);
    }

    // A role the cloud has shipped but this machine's build has never heard of.
    [Fact]
    public void An_unknown_role_gets_nothing()
    {
        var stranger = new TestUser("x", (UserRole)99);

        Assert.Empty(RolePermissions.For(stranger.Role));
        Assert.False(OrderAccessPolicy.CanCreate(stranger, OrderType.DineIn).IsAllowed);
    }
}
