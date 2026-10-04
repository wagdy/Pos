using MediatR;
using Otantik.BuildingBlocks;
using Otantik.SharedKernel.Auditing;
using Otantik.SharedKernel.Authorization;
using Otantik.SharedKernel.Orders;
using OtantikPos.Ordering.Application.Ports;
using OtantikPos.Ordering.Domain;

namespace OtantikPos.Ordering.Application.Orders;

// Points, payment and the final receipt: the cashier's side of the bill.

// Applies points to the bill after checking the balance with the delivery system. Nothing is
// spent yet: that happens at checkout.
public sealed record ApplyLoyaltyPointsCommand(Guid OrderId, int Points) : IRequest<Order>;

internal sealed class ApplyLoyaltyPointsHandler(OrderWorkflow workflow, ILoyaltyGateway loyalty)
    : IRequestHandler<ApplyLoyaltyPointsCommand, Order>
{
    public async Task<Order> Handle(ApplyLoyaltyPointsCommand request, CancellationToken cancellationToken)
    {
        var order = await workflow.LoadForAsync(OrderAction.ApplyLoyalty, request.OrderId, cancellationToken);

        // Needs the cloud. Offline, this throws DeliverySystemUnavailableException and the bill
        // stays as it was.
        var balance = await loyalty.GetBalanceAsync(order.UserId!, cancellationToken);
        var rate = await workflow.RedemptionValuePer100PointsAsync(cancellationToken);
        var tax = await workflow.TaxPercentageAsync(cancellationToken);

        TillOperations.ApplyPoints(order, request.Points, balance, rate, tax, workflow.Now);

        workflow.Audit(order, OrderAuditAction.LoyaltyApplied, order.PointsDiscountAmount, quantity: order.PointsRedeemed);
        return await workflow.CommitAsync(order, cancellationToken);
    }
}

public sealed record RemoveLoyaltyPointsCommand(Guid OrderId) : IRequest<Order>;

internal sealed class RemoveLoyaltyPointsHandler(OrderWorkflow workflow) : IRequestHandler<RemoveLoyaltyPointsCommand, Order>
{
    public async Task<Order> Handle(RemoveLoyaltyPointsCommand request, CancellationToken cancellationToken)
    {
        var order = await workflow.LoadForAsync(OrderAction.ApplyLoyalty, request.OrderId, cancellationToken);
        var tax = await workflow.TaxPercentageAsync(cancellationToken);

        TillOperations.RemovePoints(order, tax, workflow.Now);

        return await workflow.CommitAsync(order, cancellationToken);
    }
}

// Takes payment and closes the bill. In one save: the order is paid, anything not yet in the
// kitchen goes there, and OrderSettled goes to the outbox for Inventory to deduct stock.
public sealed record CheckoutCommand(Guid OrderId, PaymentMethod PaymentMethod) : IRequest<Order>;

internal sealed class CheckoutHandler(OrderWorkflow workflow, ILoyaltyGateway loyalty)
    : IRequestHandler<CheckoutCommand, Order>
{
    public async Task<Order> Handle(CheckoutCommand request, CancellationToken cancellationToken)
    {
        var order = await workflow.LoadForAsync(OrderAction.Checkout, request.OrderId, cancellationToken);
        var tax = await workflow.TaxPercentageAsync(cancellationToken);

        // Closed in memory first: every rule that can refuse checkout has had its say before any
        // points are spent.
        var sentNow = TillOperations.Close(order, request.PaymentMethod, workflow.User.UserId, tax, workflow.Now);

        // Then the points, in the delivery system. If that fails (offline, or the balance was
        // spent online meanwhile), nothing here is saved and the bill is still open. If the save
        // below fails after the points were spent, a retry spends nothing more: redemption is
        // idempotent by order.
        if (order.PointsRedeemed > 0)
            await loyalty.RedeemAsync(order.UserId!, order.PointsRedeemed, order.PublicId, cancellationToken);

        workflow.Audit(order, OrderAuditAction.PaymentTaken, order.TotalAmount);
        if (sentNow.Count > 0)
            workflow.Publish(OrderEvents.Sent(order, sentNow));
        workflow.Publish(OrderEvents.Settled(order));

        return await workflow.CommitAsync(order, cancellationToken);
    }
}

public sealed record PrintFinalReceiptCommand(Guid OrderId) : IRequest;

internal sealed class PrintFinalReceiptHandler(OrderWorkflow workflow, IReceiptPrintQueue receipts, IUnitOfWork unitOfWork)
    : IRequestHandler<PrintFinalReceiptCommand>
{
    public async Task Handle(PrintFinalReceiptCommand request, CancellationToken cancellationToken)
    {
        var order = await workflow.LoadForAsync(OrderAction.PrintFinalReceipt, request.OrderId, cancellationToken);

        await receipts.EnqueueAsync(order, workflow.User.UserId, cancellationToken);

        // Audited, so a receipt printed twice for one payment is visible later. The order itself
        // has not changed, so there is nothing to sync or push to the tills.
        workflow.Audit(order, OrderAuditAction.FinalReceiptPrinted, order.TotalAmount);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
