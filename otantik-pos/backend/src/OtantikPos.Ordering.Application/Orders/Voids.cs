using MediatR;
using Otantik.BuildingBlocks;
using Otantik.SharedKernel.Auditing;
using Otantik.SharedKernel.Authorization;
using Otantik.SharedKernel.Orders;
using OtantikPos.Ordering.Domain;

namespace OtantikPos.Ordering.Application.Orders;

// One command per scope, item or whole order, and no void type in either. The order decides
// whether this is a Void Before, a void after the kitchen or a Void After (OrderRules), and
// OrderAccessPolicy decides whether this user may do that kind. Every void leaves an audit
// entry with who, what, how much and why.

public sealed record VoidOrderItemCommand(Guid OrderId, Guid OrderItemId, int Quantity, string? Reason) : IRequest<Order>;

internal sealed class VoidOrderItemHandler(OrderWorkflow workflow) : IRequestHandler<VoidOrderItemCommand, Order>
{
    public async Task<Order> Handle(VoidOrderItemCommand request, CancellationToken cancellationToken)
    {
        var order = await workflow.LoadAsync(request.OrderId, cancellationToken);
        var item = order.OrderItems.SingleOrDefault(i => i.PublicId == request.OrderItemId)
            ?? throw new NotFoundException("Order item", request.OrderItemId);
        OrderWorkflow.Demand(OrderAccessPolicy.Evaluate(workflow.User, OrderAction.VoidItem, order, item));
        var tax = await workflow.TaxPercentageAsync(cancellationToken);

        var line = TillOperations.VoidItem(order, item, request.Quantity, request.Reason, workflow.User.UserId, tax, workflow.Now);

        Audit(workflow, order, line);
        workflow.Publish(OrderEvents.Voided(order, line.Item.VoidReason, [line]));
        if (OrderEvents.LoyaltyRefund(order, [line]) is { } refund)
            workflow.Publish(refund);
        return await workflow.CommitAsync(order, cancellationToken);
    }

    // The value that came off: the refund after payment, the line's price before it.
    internal static void Audit(OrderWorkflow workflow, Order order, VoidedLine line) =>
        workflow.Audit(
            order,
            OrderAuditAction.ItemVoided,
            line.Type == VoidType.AfterPayment ? line.RefundAmount : line.Item.LineTotal,
            line.Item,
            line.Type,
            line.Item.Quantity,
            line.Item.VoidReason);
}

// Before payment this cancels the order; after payment it refunds it in full.
public sealed record VoidOrderCommand(Guid OrderId, string? Reason) : IRequest<Order>;

internal sealed class VoidOrderHandler(OrderWorkflow workflow) : IRequestHandler<VoidOrderCommand, Order>
{
    public async Task<Order> Handle(VoidOrderCommand request, CancellationToken cancellationToken)
    {
        var order = await workflow.LoadForAsync(OrderAction.VoidOrder, request.OrderId, cancellationToken);
        var tax = await workflow.TaxPercentageAsync(cancellationToken);

        var lines = TillOperations.VoidOrder(order, request.Reason, workflow.User.UserId, tax, workflow.Now);

        foreach (var line in lines)
            VoidOrderItemHandler.Audit(workflow, order, line);
        workflow.Audit(order, OrderAuditAction.OrderVoided, lines.Sum(l => l.Type == VoidType.AfterPayment ? l.RefundAmount : l.Item.LineTotal), reason: request.Reason);

        if (lines.Count > 0)
            workflow.Publish(OrderEvents.Voided(order, lines[0].Item.VoidReason, lines));
        if (OrderEvents.LoyaltyRefund(order, lines) is { } refund)
            workflow.Publish(refund);
        return await workflow.CommitAsync(order, cancellationToken);
    }
}
