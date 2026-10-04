using Microsoft.AspNetCore.SignalR;
using OtantikPos.Node.Infrastructure.DeliverySystem;

namespace OtantikPos.Node.Api.Hubs;

// Tells every till when this machine's link to the delivery system changes, so the cashier sees
// "cloud offline" and the cloud-only actions switch off the moment it happens. A till that was
// not connected at that moment reads GET /api/status when it (re)connects.
internal sealed class DeliverySystemLinkBroadcaster(
    DeliverySystemLink link,
    IHubContext<TillHub, ITillClient> hub,
    ILogger<DeliverySystemLinkBroadcaster> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        link.Changed += Broadcast;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        link.Changed -= Broadcast;
        return Task.CompletedTask;
    }

    // Raised on the listener's thread, which must not wait on the tills.
    private void Broadcast(DeliverySystemLinkStatus status) => _ = BroadcastAsync(status);

    private async Task BroadcastAsync(DeliverySystemLinkStatus status)
    {
        try
        {
            await hub.Clients.All.DeliverySystemLinkChanged(status);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not tell the tills the delivery system is {State}", status.State);
        }
    }
}
