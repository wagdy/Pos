using MediatR;
using OtantikPos.Ordering.Application.Ports;
using OtantikPos.Ordering.Contracts;

namespace OtantikPos.Ordering.Application.Orders;

// Settles a Void After with the customer's wallet in the delivery system. Runs from the outbox,
// after the refund has been saved at the till, and again on every retry until the delivery
// system accepts it: offline it throws, and the outbox keeps the event. The cashier's refund
// never waits on this. The event's id is the refund's id, so a repeat changes nothing.
internal sealed class ReturnLoyaltyPointsHandler(ILoyaltyGateway loyalty) : INotificationHandler<LoyaltyRefundDue>
{
    public Task Handle(LoyaltyRefundDue notification, CancellationToken cancellationToken) =>
        loyalty.RefundAsync(
            notification.CustomerUserId,
            notification.OrderPublicId,
            notification.EventId,
            notification.PointsToReturn,
            notification.RefundedAmount,
            cancellationToken);
}
