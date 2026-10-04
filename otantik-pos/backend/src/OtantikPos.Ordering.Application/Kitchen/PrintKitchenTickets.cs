using MediatR;
using Otantik.SharedKernel.Orders;
using OtantikPos.Ordering.Application.Ports;
using OtantikPos.Ordering.Contracts;

namespace OtantikPos.Ordering.Application.Kitchen;

internal sealed class PrintKitchenTickets(IKitchenPrintQueue printQueue, IOrderStore orders) :
    INotificationHandler<ItemsSentToKitchen>,
    INotificationHandler<OrderItemsVoided>
{
    public async Task Handle(ItemsSentToKitchen notification, CancellationToken cancellationToken)
    {
        var label = await LabelAsync(notification.OrderPublicId, cancellationToken);

        await printQueue.EnqueueAsync(
            new KitchenTicket(
                notification.EventId,
                label,
                KitchenTicketKind.New,
                notification.Lines.Select(l => new KitchenTicketLine(l.Name, l.Quantity, l.AddOns, l.Notes)).ToList(),
                Reason: null),
            cancellationToken);
    }

    // A void slip only for items voided after reaching the kitchen. A Void Before never got
    // there; after payment the dish has usually been served.
    public async Task Handle(OrderItemsVoided notification, CancellationToken cancellationToken)
    {
        var lines = notification.Lines
            .Where(l => l.Stage == VoidStage.AfterKitchen)
            .Select(l => new KitchenTicketLine(l.Name, l.Quantity, [], Notes: null))
            .ToList();
        if (lines.Count == 0)
            return;

        var label = await LabelAsync(notification.OrderPublicId, cancellationToken);
        await printQueue.EnqueueAsync(
            new KitchenTicket(notification.EventId, label, KitchenTicketKind.Void, lines, notification.Reason),
            cancellationToken);
    }

    // What the kitchen calls out. A table for dine-in; otherwise the order's number in this
    // database, which the events cannot carry because it is assigned on save.
    private async Task<string> LabelAsync(Guid orderPublicId, CancellationToken cancellationToken)
    {
        var order = await orders.GetAsync(orderPublicId, cancellationToken);
        return order switch
        {
            null => "Order",
            { Type: OrderType.DineIn } => $"Table {order.TableNumber}",
            { Type: OrderType.Takeaway } => $"Takeaway #{order.Id}",
            _ => $"Delivery #{order.Id}",
        };
    }
}
