using MediatR;
using Microsoft.Extensions.Logging;
using Otantik.SharedKernel.Orders;
using OtantikPos.Ordering.Application.Ports;
using OtantikPos.Ordering.Domain;

namespace OtantikPos.Ordering.Application.Orders;

// An order pushed down from the delivery system: a captain's dine-in order, an online order,
// or either one changed in the cloud. Sent by Infrastructure's cloud sync, which keeps a
// SignalR connection open to the delivery system (outbound: the cloud cannot reach into the
// restaurant's network) and catches up on anything missed after a reconnect.
//
// It arrives as the shared Order itself, exactly the JSON the delivery system produced, and
// is stored as it is. That is the point of sharing the model. CloudOrderMerge decides how a
// change lands on a copy the till already has.
//
// No user is involved, and so no access check. This is the system receiving data, not a
// person acting.
public sealed record ReceiveCloudOrderCommand(Order Order) : IRequest<Order>;

internal sealed class ReceiveCloudOrderHandler(
    OrderWorkflow workflow,
    IOrderStore orders,
    ILogger<ReceiveCloudOrderHandler> logger) : IRequestHandler<ReceiveCloudOrderCommand, Order>
{
    public async Task<Order> Handle(ReceiveCloudOrderCommand request, CancellationToken cancellationToken)
    {
        var incoming = request.Order;
        var local = await orders.GetAsync(incoming.PublicId, cancellationToken);

        if (local is null)
        {
            var order = CloudOrderMerge.Import(incoming);
            workflow.Add(order);

            // A captain's submitted items arrive already marked sent. This till holds the only
            // connection to the kitchen printer, so it prints them.
            var toPrint = order.OrderItems.Where(i => i.IsSentToKitchen && !i.IsVoided).ToList();
            if (toPrint.Count > 0)
                workflow.Publish(OrderEvents.Sent(order, toPrint));

            return await workflow.CommitAsync(order, cancellationToken, syncToCloud: false);
        }

        var tax = await workflow.TaxPercentageAsync(cancellationToken);
        var result = CloudOrderMerge.Merge(local, incoming, tax, workflow.Now);

        if (result.Ignored is not null)
            logger.LogWarning("Cloud change to order {OrderPublicId} not fully applied: {Reason}", incoming.PublicId, result.Ignored);
        if (!result.Changed)
            return local;

        if (result.NewlySent.Count > 0)
            workflow.Publish(OrderEvents.Sent(local, result.NewlySent));
        if (result.Cancelled.Count > 0)
            workflow.Publish(OrderEvents.Voided(local, result.Cancelled[0].Item.VoidReason, result.Cancelled));

        return await workflow.CommitAsync(local, cancellationToken, syncToCloud: false);
    }
}
