using MediatR;
using Microsoft.Extensions.Options;
using OtantikPos.Ordering.Application.Ports;
using OtantikPos.Ordering.Contracts;

namespace OtantikPos.Node.Infrastructure.DeliverySystem;

// Sends the till's copy of an order to the delivery system after every change: its voids,
// payment and refunds, and the order itself into the customer's history. Runs from the outbox.
//
// Offline, the push throws, and the outbox keeps the message and tries again with a backoff
// until the internet returns (CloudOrderListener hurries it along on reconnect). It always sends
// the order as it is now, not as it was when the change happened, so a backlog of pushes for
// one order all carry the same, latest state. Sending one twice is harmless: the delivery
// system upserts by PublicId.
internal sealed class PushOrderToDeliverySystem(IOrderStore orders, DeliverySystemApi api, IOptions<DeliverySystemOptions> options)
    : INotificationHandler<OrderChanged>
{
    public async Task Handle(OrderChanged notification, CancellationToken cancellationToken)
    {
        // A standalone till has nowhere to push to, and retrying forever would only fill the
        // outbox.
        if (!options.Value.IsConfigured)
            return;

        var order = await orders.GetAsync(notification.OrderPublicId, cancellationToken);
        if (order is not null)
            await api.PushOrderAsync(order, cancellationToken);
    }
}
