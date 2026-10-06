using Microsoft.AspNetCore.SignalR;
using OtantikPos.Node.Infrastructure.Printing;

namespace OtantikPos.Node.Api.Hubs;

// Tells every till when a printer stops printing or starts again, so the cashier sees "kitchen
// printer: 2 waiting" rather than learning from the kitchen. A till that was not connected at
// that moment reads GET /api/status when it (re)connects.
internal sealed class PrinterStatusBroadcaster(
    PrinterStatus printers,
    IHubContext<TillHub, ITillClient> hub,
    ILogger<PrinterStatusBroadcaster> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        printers.Changed += Broadcast;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        printers.Changed -= Broadcast;
        return Task.CompletedTask;
    }

    // Raised on the print worker's thread, which must not wait on the tills.
    private void Broadcast(IReadOnlyList<PrinterProblem> problems) => _ = BroadcastAsync(problems);

    private async Task BroadcastAsync(IReadOnlyList<PrinterProblem> problems)
    {
        try
        {
            await hub.Clients.All.PrintersChanged(problems);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not tell the tills about the printers");
        }
    }
}
