using Otantik.BuildingBlocks;
using Otantik.SharedKernel.Auditing;
using Otantik.SharedKernel.Authorization;
using Otantik.SharedKernel.Orders;
using OtantikPos.Ordering.Application.Ports;
using OtantikPos.Ordering.Contracts;

namespace OtantikPos.Ordering.Application;

// The steps every order command repeats, in one place: load the order, check the user may do
// this to it now, record the audit entry, publish events, save, and tell the tills. A handler
// then only says what is different about it.
public sealed class OrderWorkflow(
    IOrderStore orders,
    ICurrentUser currentUser,
    IAuditLog auditLog,
    IEventOutbox outbox,
    IUnitOfWork unitOfWork,
    ITillNotifier tills,
    IPricingSettings pricing,
    TimeProvider time)
{
    public ICurrentUser User => currentUser;

    public DateTime Now => time.GetUtcNow().UtcDateTime;

    public Task<decimal> TaxPercentageAsync(CancellationToken cancellationToken) =>
        pricing.GetTaxPercentageAsync(cancellationToken);

    public Task<decimal> RedemptionValuePer100PointsAsync(CancellationToken cancellationToken) =>
        pricing.GetRedemptionValuePer100PointsAsync(cancellationToken);

    public async Task<Order> LoadAsync(Guid orderPublicId, CancellationToken cancellationToken) =>
        await orders.GetAsync(orderPublicId, cancellationToken)
            ?? throw new NotFoundException("Order", orderPublicId);

    // Loads the order and refuses unless OrderAccessPolicy allows this user to do this to it now.
    public async Task<Order> LoadForAsync(OrderAction action, Guid orderPublicId, CancellationToken cancellationToken)
    {
        var order = await LoadAsync(orderPublicId, cancellationToken);
        Demand(OrderAccessPolicy.Evaluate(currentUser, action, order));
        return order;
    }

    public static void Demand(AccessDecision decision)
    {
        if (!decision.IsAllowed)
            throw new ForbiddenException(decision.Reason ?? "You are not allowed to do that.");
    }

    public void Add(Order order) => orders.Add(order);

    public void Audit(
        Order order, OrderAuditAction action, decimal amount = 0, OrderItem? item = null,
        VoidType? voidType = null, int? quantity = null, string? reason = null) =>
        auditLog.Add(OrderAuditEntry.For(order, action, currentUser, amount, item, voidType, quantity, reason));

    public void Publish(OrderingEvent orderingEvent) => outbox.Add(orderingEvent);

    // Saves the order, its audit entries and its events in one transaction, then pushes the
    // new state to every till.
    //
    // syncToCloud is false only for a change that came from the cloud. Sending it back would
    // make the cloud broadcast it again, and the two would echo the order between them.
    public async Task<Order> CommitAsync(Order order, CancellationToken cancellationToken, bool syncToCloud = true)
    {
        if (syncToCloud)
            outbox.Add(new OrderChanged(order.PublicId));

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await tills.OrderChangedAsync(order, cancellationToken);
        return order;
    }
}
