using Otantik.BuildingBlocks;
using Otantik.SharedKernel.Identity;
using Otantik.SharedKernel.Orders;
using OtantikPos.Ordering.Application.Orders;
using OtantikPos.Ordering.Contracts;

namespace OtantikPos.Application.Tests;

// A captain takes a dine-in order in the delivery app (cloud). It arrives on the till as the
// shared Order itself, and each later change in the cloud merges onto the till's copy.
public class CloudSyncTests
{
    private readonly TestPos _pos = new();

    // What the delivery system would send: a captain's submitted order, ids from its own
    // database, items already marked sent.
    private static Order CaptainOrderFromCloud(bool sent = true) => new()
    {
        Id = 9001,
        Type = OrderType.DineIn,
        TableNumber = "7",
        CreatedByUserId = "captain-1",
        Status = OrderStatus.Pending,
        PaymentStatus = PaymentStatus.Pending,
        OrderItems =
        [
            new OrderItem
            {
                Id = 555, MenuItemId = TestPos.Burger, MenuItemName = "Burger", UnitPrice = 120, Quantity = 1,
                SentToKitchenAt = sent ? DateTime.UtcNow : null,
            },
        ],
    };

    [Fact]
    public async Task A_captains_order_from_the_cloud_lands_on_the_till_and_prints_once()
    {
        var fromCloud = CaptainOrderFromCloud();

        var order = await _pos.Send(new ReceiveCloudOrderCommand(fromCloud));
        await _pos.Send(new ReceiveCloudOrderCommand(CaptainOrderFromCloud().SameAs(fromCloud)));
        await _pos.DeliverEventsAsync();

        Assert.NotEqual(9001, order.Id);
        Assert.Contains((await _pos.Send(new GetOpenOrdersQuery())), o => o.PublicId == fromCloud.PublicId);
        var ticket = Assert.Single(_pos.Sink.KitchenTickets);
        Assert.Equal("Table 7", ticket.OrderLabel);

        // The till never sends a cloud change back: that would echo forever.
        Assert.Empty(_pos.Sink.Committed<OrderChanged>());
    }

    [Fact]
    public async Task The_captains_next_round_is_merged_and_printed()
    {
        var fromCloud = CaptainOrderFromCloud();
        await _pos.Send(new ReceiveCloudOrderCommand(fromCloud));

        var nextRound = CaptainOrderFromCloud().SameAs(fromCloud);
        nextRound.OrderItems.Add(new OrderItem { MenuItemId = TestPos.Burger, MenuItemName = "Burger", UnitPrice = 120, Quantity = 2, SentToKitchenAt = DateTime.UtcNow });
        var order = await _pos.Send(new ReceiveCloudOrderCommand(nextRound));
        await _pos.DeliverEventsAsync();

        Assert.Equal(2, order.OrderItems.Count);
        Assert.Equal(410.40m, order.TotalAmount);
        Assert.Equal(2, _pos.Sink.KitchenTickets.Count);
        Assert.Equal(2, _pos.Sink.KitchenTickets[1].Lines.Single().Quantity);
    }

    // The till owns voids and money. A cloud message still showing the item unvoided cannot
    // undo the cashier's void, and nothing from the cloud touches a paid order.
    [Fact]
    public async Task The_tills_voids_and_payments_survive_later_cloud_messages()
    {
        var fromCloud = CaptainOrderFromCloud();
        var order = await _pos.Send(new ReceiveCloudOrderCommand(fromCloud));

        order = await _pos.Send(new VoidOrderItemCommand(order.PublicId, order.OrderItems.Single().PublicId, 1, "dropped"));
        order = await _pos.Send(new ReceiveCloudOrderCommand(CaptainOrderFromCloud().SameAs(fromCloud)));
        Assert.Equal(VoidType.AfterKitchen, order.OrderItems.Single().VoidType);

        await _pos.Send(new AddOrderItemCommand(order.PublicId, TestPos.Burger));
        order = await _pos.Send(new CheckoutCommand(order.PublicId, PaymentMethod.Cash));

        var late = CaptainOrderFromCloud().SameAs(fromCloud);
        late.TableNumber = "99";
        order = await _pos.Send(new ReceiveCloudOrderCommand(late));
        Assert.Equal("7", order.TableNumber);
        Assert.True(OrderRules.IsSettled(order));
    }

    [Fact]
    public async Task A_cloud_cancellation_applies_only_while_nothing_has_reached_the_kitchen()
    {
        var notSent = CaptainOrderFromCloud(sent: false);
        await _pos.Send(new ReceiveCloudOrderCommand(notSent));
        var cancelNotSent = CaptainOrderFromCloud(sent: false).SameAs(notSent);
        cancelNotSent.Status = OrderStatus.Cancelled;
        Assert.Equal(OrderStatus.Cancelled, (await _pos.Send(new ReceiveCloudOrderCommand(cancelNotSent))).Status);

        var sent = CaptainOrderFromCloud();
        await _pos.Send(new ReceiveCloudOrderCommand(sent));
        var cancelSent = CaptainOrderFromCloud().SameAs(sent);
        cancelSent.Status = OrderStatus.Cancelled;
        var order = await _pos.Send(new ReceiveCloudOrderCommand(cancelSent));
        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.True(OrderRules.IsOpen(order));
    }

    // Cash on delivery: the driver hands the food over and marks it delivered in the delivery
    // app, then brings the money back. Until the cashier takes it, the order stays on the
    // till's list and can still be paid, though nothing more can be added to it.
    [Fact]
    public async Task A_delivery_marked_delivered_in_the_cloud_stays_on_the_till_until_paid()
    {
        var order = await _pos.Send(new OpenOrderCommand(OrderType.Delivery, CustomerPhone: "01001234567", DeliveryAddress: "12 Nile Street", DeliveryFee: 20));
        await _pos.Send(new AddOrderItemCommand(order.PublicId, TestPos.Burger));
        order = await _pos.Send(new SendToKitchenCommand(order.PublicId));

        var delivered = _pos.Orders.Saved(order.PublicId);
        delivered.Status = OrderStatus.Delivered;
        await _pos.Send(new ReceiveCloudOrderCommand(delivered));

        Assert.Equal([order.PublicId], (await _pos.Send(new GetOpenOrdersQuery())).Select(o => o.PublicId));
        await Assert.ThrowsAsync<ForbiddenException>(() => _pos.Send(new AddOrderItemCommand(order.PublicId, TestPos.Burger)));

        var paid = await _pos.Send(new CheckoutCommand(order.PublicId, PaymentMethod.Cash));

        Assert.True(OrderRules.IsSettled(paid));
        Assert.Equal(OrderStatus.Delivered, paid.Status);
        Assert.Empty(await _pos.Send(new GetOpenOrdersQuery()));
    }

    [Fact]
    public async Task Status_only_moves_forward()
    {
        var fromCloud = CaptainOrderFromCloud();
        fromCloud.Status = OrderStatus.Preparing;
        await _pos.Send(new ReceiveCloudOrderCommand(fromCloud));

        var stale = CaptainOrderFromCloud().SameAs(fromCloud);
        stale.Status = OrderStatus.Pending;

        Assert.Equal(OrderStatus.Preparing, (await _pos.Send(new ReceiveCloudOrderCommand(stale))).Status);
    }
}

internal static class CloudOrderTestExtensions
{
    // The same order sent again: same PublicIds, everything else as freshly built.
    public static Order SameAs(this Order copy, Order original)
    {
        copy.PublicId = original.PublicId;
        foreach (var (item, originalItem) in copy.OrderItems.Zip(original.OrderItems))
            item.PublicId = originalItem.PublicId;
        return copy;
    }
}
