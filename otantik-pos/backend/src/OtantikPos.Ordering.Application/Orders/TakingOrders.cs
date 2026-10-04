using MediatR;
using Otantik.BuildingBlocks;
using Otantik.SharedKernel.Auditing;
using Otantik.SharedKernel.Authorization;
using Otantik.SharedKernel.Orders;
using OtantikPos.Ordering.Application.Ports;
using OtantikPos.Ordering.Domain;

namespace OtantikPos.Ordering.Application.Orders;

// Opening an order, adding to it, and sending it to the kitchen: everything a captain may do.
// Every handler returns the shared Order itself, which is what the API sends to the tills.
// There is no separate response model to map to.

public sealed record OpenOrderCommand(
    OrderType Type,
    string? TableNumber = null,
    string? CustomerPhone = null,
    string? CustomerName = null,
    string? DeliveryAddress = null,
    decimal DeliveryFee = 0,
    string? Notes = null) : IRequest<Order>;

internal sealed class OpenOrderHandler(OrderWorkflow workflow, ICustomerDirectory customers)
    : IRequestHandler<OpenOrderCommand, Order>
{
    public async Task<Order> Handle(OpenOrderCommand request, CancellationToken cancellationToken)
    {
        OrderWorkflow.Demand(OrderAccessPolicy.CanCreate(workflow.User, request.Type));

        var order = TillOperations.Open(
            new OpenOrderDetails(request.Type, request.TableNumber, request.CustomerName, request.CustomerPhone, request.DeliveryAddress, request.DeliveryFee, request.Notes),
            workflow.User.UserId,
            workflow.Now);

        // A takeaway or delivery starts from the customer's mobile number, so the order belongs
        // to their account from the start. Best effort: if the delivery system cannot be
        // reached, the order opens as a walk-in with the number on it, and AttachCustomer can
        // try again.
        if (!string.IsNullOrWhiteSpace(request.CustomerPhone))
        {
            var lookup = await CustomerLookup.FindAsync(customers, request.CustomerPhone, cancellationToken);
            if (lookup.Profile is not null)
            {
                var tax = await workflow.TaxPercentageAsync(cancellationToken);
                TillOperations.AttachCustomer(order, lookup.Profile, request.CustomerPhone, request.CustomerName, tax, workflow.Now);
            }
        }

        workflow.Add(order);
        workflow.Audit(order, OrderAuditAction.Created);
        return await workflow.CommitAsync(order, cancellationToken);
    }
}

public sealed record AddOrderItemCommand(
    Guid OrderId,
    int MenuItemId,
    int Quantity = 1,
    int? VariantId = null,
    IReadOnlyList<int>? AddOnIds = null,
    string? Notes = null) : IRequest<Order>;

internal sealed class AddOrderItemHandler(OrderWorkflow workflow, ICatalog catalog)
    : IRequestHandler<AddOrderItemCommand, Order>
{
    public async Task<Order> Handle(AddOrderItemCommand request, CancellationToken cancellationToken)
    {
        var order = await workflow.LoadForAsync(OrderAction.AddItems, request.OrderId, cancellationToken);
        var menuItem = await catalog.GetMenuItemAsync(request.MenuItemId, cancellationToken)
            ?? throw new NotFoundException("Menu item", request.MenuItemId);
        var tax = await workflow.TaxPercentageAsync(cancellationToken);

        var item = TillOperations.AddItem(order, menuItem, request.VariantId, request.AddOnIds ?? [], request.Quantity, request.Notes, tax, workflow.Now);

        workflow.Audit(order, OrderAuditAction.ItemsAdded, item.LineTotal, item, quantity: item.Quantity);
        return await workflow.CommitAsync(order, cancellationToken);
    }
}

// "Submit": the captain's way of saying the table has ordered. Prints whatever is not yet in
// the kitchen. The ticket prints when the event is delivered, after the save, so nothing
// prints for a change that failed to save.
public sealed record SendToKitchenCommand(Guid OrderId) : IRequest<Order>;

internal sealed class SendToKitchenHandler(OrderWorkflow workflow) : IRequestHandler<SendToKitchenCommand, Order>
{
    public async Task<Order> Handle(SendToKitchenCommand request, CancellationToken cancellationToken)
    {
        var order = await workflow.LoadForAsync(OrderAction.SendToKitchen, request.OrderId, cancellationToken);

        var sent = TillOperations.SendToKitchen(order, workflow.Now);
        if (sent.Count == 0)
            return order;

        workflow.Audit(order, OrderAuditAction.SentToKitchen, quantity: sent.Sum(i => i.Quantity));
        workflow.Publish(OrderEvents.Sent(order, sent));
        return await workflow.CommitAsync(order, cancellationToken);
    }
}
