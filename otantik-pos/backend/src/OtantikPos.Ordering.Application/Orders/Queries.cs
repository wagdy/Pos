using MediatR;
using Otantik.BuildingBlocks;
using Otantik.SharedKernel.Authorization;
using Otantik.SharedKernel.Orders;
using OtantikPos.Ordering.Application.Ports;

namespace OtantikPos.Ordering.Application.Orders;

public sealed record GetOrderQuery(Guid OrderId) : IRequest<Order>;

internal sealed class GetOrderHandler(OrderWorkflow workflow) : IRequestHandler<GetOrderQuery, Order>
{
    public Task<Order> Handle(GetOrderQuery request, CancellationToken cancellationToken) =>
        workflow.LoadForAsync(OrderAction.View, request.OrderId, cancellationToken);
}

// The tills' shared view of the floor. A captain sees only the orders they created.
public sealed record GetOpenOrdersQuery : IRequest<IReadOnlyList<Order>>;

internal sealed class GetOpenOrdersHandler(IOrderStore orders, ICurrentUser user)
    : IRequestHandler<GetOpenOrdersQuery, IReadOnlyList<Order>>
{
    public async Task<IReadOnlyList<Order>> Handle(GetOpenOrdersQuery request, CancellationToken cancellationToken)
    {
        if (!user.Has(Permissions.OrderViewAll) && !user.Has(Permissions.OrderViewOwn))
            throw new ForbiddenException("You are not allowed to see orders.");

        var open = await orders.GetOpenAsync(cancellationToken);
        return user.Has(Permissions.OrderViewAll)
            ? open
            : open.Where(o => o.CreatedByUserId == user.UserId).ToList();
    }
}

// Every order from the last `Hours`, any state: where a paid order is found for a Void After.
public sealed record GetRecentOrdersQuery(int Hours = 24) : IRequest<IReadOnlyList<Order>>;

internal sealed class GetRecentOrdersHandler(IOrderStore orders, ICurrentUser user, TimeProvider time)
    : IRequestHandler<GetRecentOrdersQuery, IReadOnlyList<Order>>
{
    public Task<IReadOnlyList<Order>> Handle(GetRecentOrdersQuery request, CancellationToken cancellationToken)
    {
        if (!user.Has(Permissions.OrderViewAll))
            throw new ForbiddenException("You are not allowed to see all orders.");

        var since = time.GetUtcNow().UtcDateTime.AddHours(-Math.Clamp(request.Hours, 1, 24 * 7));
        return orders.GetCreatedSinceAsync(since, cancellationToken);
    }
}
