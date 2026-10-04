using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Otantik.SharedKernel.Authorization;
using Otantik.SharedKernel.Orders;
using OtantikPos.Node.Api.Auth;
using OtantikPos.Node.Infrastructure.DeliverySystem;
using OtantikPos.Ordering.Application.Ports;

namespace OtantikPos.Node.Api.Hubs;

// Keeps every till in step. When an order changes, whether at a till, from a captain in the
// delivery app, or online, every till that may see it receives the new state at once: the
// shared Order, the same JSON the REST endpoints return.
//
// Push only: tills change orders through the REST endpoints, never through the hub. A push
// missed while disconnected is not replayed, so a till that reconnects reloads its open orders
// once.
[Authorize]
public sealed class TillHub : Hub<ITillClient>
{
    public const string Path = "/hubs/till";

    internal const string EveryOrder = "orders:all";

    internal static string OrdersCreatedBy(string userId) => $"orders:created-by:{userId}";

    // The same rule as GetOpenOrdersQuery: whoever may see every order gets every push, and
    // whoever may see only their own gets only those. Groups do not survive a reconnect;
    // this runs again on each one.
    public override async Task OnConnectedAsync()
    {
        var user = Context.User!;
        var role = StaffClaims.RoleOf(user);

        if (role is { } all && RolePermissions.Has(all, Permissions.OrderViewAll))
            await Groups.AddToGroupAsync(Context.ConnectionId, EveryOrder);
        else if (role is { } own && RolePermissions.Has(own, Permissions.OrderViewOwn) && StaffClaims.IdOf(user) is { } userId)
            await Groups.AddToGroupAsync(Context.ConnectionId, OrdersCreatedBy(userId));

        await base.OnConnectedAsync();
    }
}

public interface ITillClient
{
    Task OrderChanged(Order order);

    // This machine's link to the delivery system came up or went down. To every till: what
    // needs the cloud (customer lookup, points) is switched on or off by it.
    Task DeliverySystemLinkChanged(DeliverySystemLinkStatus status);
}

// Ordering's ITillNotifier. OrderWorkflow calls it after every commit; the cloud listener and
// the outbox reach it too, outside any request, which IHubContext handles.
internal sealed class TillNotifier(IHubContext<TillHub, ITillClient> hub, ILogger<TillNotifier> logger) : ITillNotifier
{
    // Never throws: the change is saved, and a till that missed the push reloads on reconnect.
    public async Task OrderChangedAsync(Order order, CancellationToken cancellationToken)
    {
        try
        {
            await hub.Clients.Group(TillHub.EveryOrder).OrderChanged(order);
            if (order.CreatedByUserId is { } creator)
                await hub.Clients.Group(TillHub.OrdersCreatedBy(creator)).OrderChanged(order);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not push order {OrderPublicId} to the tills; they will pick it up when they reload", order.PublicId);
        }
    }
}
