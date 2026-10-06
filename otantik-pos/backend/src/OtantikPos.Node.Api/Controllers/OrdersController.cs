using MediatR;
using Microsoft.AspNetCore.Mvc;
using Otantik.SharedKernel.Orders;
using OtantikPos.Ordering.Application.Orders;

namespace OtantikPos.Node.Api.Controllers;

// Every endpoint returns the shared Order: the same JSON the delivery system produces and the
// till hub pushes. Orders are addressed by PublicId, the id both systems agree on; `id` in the
// JSON is this database's own row number.
//
// No [Authorize(Policy = ...)] here. Who may do what to an order depends on the order (a
// captain works only on their own tables; a Void After needs it paid; points need a customer),
// so OrderAccessPolicy decides inside each use case, and a refusal comes back as a 403 with
// the reason. Each change is saved, then pushed to every till by the use case itself.
[ApiController]
[Route("api/orders")]
public sealed class OrdersController(ISender sender) : ControllerBase
{
    // The floor: every open order, oldest first.
    [HttpGet("open")]
    public Task<IReadOnlyList<Order>> GetOpen(CancellationToken cancellationToken) =>
        sender.Send(new GetOpenOrdersQuery(), cancellationToken);

    // Any state, newest first: where a paid order is found for a Void After.
    [HttpGet("recent")]
    public Task<IReadOnlyList<Order>> GetRecent([FromQuery] int hours = 24, CancellationToken cancellationToken = default) =>
        sender.Send(new GetRecentOrdersQuery(hours), cancellationToken);

    [HttpGet("{orderId:guid}")]
    public Task<Order> Get(Guid orderId, CancellationToken cancellationToken) =>
        sender.Send(new GetOrderQuery(orderId), cancellationToken);

    // Takeaway and delivery need CustomerPhone; the customer's account and points are looked
    // up in the delivery system from it, and the order opens as a walk-in if that is offline.
    [HttpPost]
    public async Task<ActionResult<Order>> Open(OpenOrderCommand command, CancellationToken cancellationToken)
    {
        var order = await sender.Send(command, cancellationToken);
        return CreatedAtAction(nameof(Get), new { orderId = order.PublicId }, order);
    }

    [HttpPost("{orderId:guid}/items")]
    public Task<Order> AddItem(Guid orderId, AddOrderItemRequest request, CancellationToken cancellationToken) =>
        sender.Send(new AddOrderItemCommand(orderId, request.MenuItemId, request.Quantity, request.VariantId, request.AddOnIds, request.Notes), cancellationToken);

    // A captain's "submit": prints whatever is not yet in the kitchen.
    [HttpPost("{orderId:guid}/send-to-kitchen")]
    public Task<Order> SendToKitchen(Guid orderId, CancellationToken cancellationToken) =>
        sender.Send(new SendToKitchenCommand(orderId), cancellationToken);

    // `lookup` tells "a walk-in" (NotFound) from "the delivery system is unreachable, try again"
    // (Unavailable). Either way the number is kept on the order.
    [HttpPut("{orderId:guid}/customer")]
    public Task<AttachCustomerResult> AttachCustomer(Guid orderId, AttachCustomerRequest request, CancellationToken cancellationToken) =>
        sender.Send(new AttachCustomerCommand(orderId, request.PhoneNumber, request.Name), cancellationToken);

    // Discount = Points / 10. Checked against the balance in the delivery system now, and spent
    // there at checkout.
    [HttpPut("{orderId:guid}/loyalty-points")]
    public Task<Order> ApplyLoyaltyPoints(Guid orderId, ApplyLoyaltyPointsRequest request, CancellationToken cancellationToken) =>
        sender.Send(new ApplyLoyaltyPointsCommand(orderId, request.Points), cancellationToken);

    [HttpDelete("{orderId:guid}/loyalty-points")]
    public Task<Order> RemoveLoyaltyPoints(Guid orderId, CancellationToken cancellationToken) =>
        sender.Send(new RemoveLoyaltyPointsCommand(orderId), cancellationToken);

    [HttpPost("{orderId:guid}/checkout")]
    public Task<Order> Checkout(Guid orderId, CheckoutRequest request, CancellationToken cancellationToken) =>
        sender.Send(new CheckoutCommand(orderId, request.PaymentMethod, request.ExpectedTotal, request.CashReceived), cancellationToken);

    // Queued for the receipt printer; 202 because it prints after this returns. Each call is
    // a copy, and each copy is in the audit trail.
    [HttpPost("{orderId:guid}/receipt")]
    public async Task<IActionResult> PrintFinalReceipt(Guid orderId, CancellationToken cancellationToken)
    {
        await sender.Send(new PrintFinalReceiptCommand(orderId), cancellationToken);
        return Accepted();
    }

    // Void Before, Void After Kitchen or Void After Payment: which one is decided by the
    // order's state, not by the caller.
    [HttpPost("{orderId:guid}/items/{orderItemId:guid}/void")]
    public Task<Order> VoidItem(Guid orderId, Guid orderItemId, VoidItemRequest request, CancellationToken cancellationToken) =>
        sender.Send(new VoidOrderItemCommand(orderId, orderItemId, request.Quantity, request.Reason), cancellationToken);

    [HttpPost("{orderId:guid}/void")]
    public Task<Order> Void(Guid orderId, VoidOrderRequest request, CancellationToken cancellationToken) =>
        sender.Send(new VoidOrderCommand(orderId, request.Reason), cancellationToken);
}

// Bodies for the endpoints whose order id comes from the route. The others take their use
// case's command as the body itself.
public sealed record AddOrderItemRequest(int MenuItemId, int Quantity = 1, int? VariantId = null, IReadOnlyList<int>? AddOnIds = null, string? Notes = null);

public sealed record AttachCustomerRequest(string PhoneNumber, string? Name = null);

public sealed record ApplyLoyaltyPointsRequest(int Points);

public sealed record CheckoutRequest(PaymentMethod PaymentMethod, decimal? ExpectedTotal = null, decimal? CashReceived = null);

public sealed record VoidItemRequest(int Quantity, string? Reason);

public sealed record VoidOrderRequest(string? Reason);
