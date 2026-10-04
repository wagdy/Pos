using System.Globalization;
using System.Text.Json.Serialization;
using MediatR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Otantik.SharedKernel.Orders;
using OtantikPos.Node.Infrastructure.Common;
using OtantikPos.Node.Infrastructure.Persistence;
using OtantikPos.Ordering.Application.Orders;
using OtantikPos.Ordering.Contracts;

namespace OtantikPos.Node.Infrastructure.DeliverySystem;

// Keeps a SignalR connection open to the delivery system and hands every order it pushes
// (a captain's dine-in order, an online order, a change to either) to ReceiveCloudOrderCommand.
//
// Outbound from the restaurant: the cloud cannot connect into the restaurant's network, so the
// till dials out and the cloud talks back over that connection.
//
// A push that arrives while the till is disconnected is lost, so the listener also catches up:
// on every (re)connect, and every few minutes besides, it asks for every order changed since it
// last asked. Merging is idempotent, so seeing an order twice does nothing.
//
// The connection is also the restaurant's heartbeat. The till pings every KeepAliveInterval,
// and the delivery system counts the restaurant offline when a ClientTimeoutInterval (its
// default 30 seconds) passes without one, which is when it stops captains taking orders: a
// captain's order needs this machine to receive it. The till, the other way round, counts the
// cloud gone after ServerTimeout, and DeliverySystemLink tells the tills.
public sealed class CloudOrderListener(
    IServiceScopeFactory scopeFactory,
    IOptions<DeliverySystemOptions> options,
    OutboxSignal outboxSignal,
    DeliverySystemLink link,
    ILogger<CloudOrderListener> logger) : BackgroundService
{
    // Well inside the delivery system's 30-second ClientTimeoutInterval, so one late ping never
    // marks an open restaurant offline. ServerTimeout is SignalR's default, written out because
    // the cloud's KeepAliveInterval (default 15 seconds) must stay under half of it.
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ServerTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan CatchUpInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FirstCatchUpWindow = TimeSpan.FromHours(24);

    // One order at a time. A live push and a catch-up can carry the same order at once, and
    // applying both in parallel would race on its row.
    private readonly SemaphoreSlim _applying = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.IsConfigured)
        {
            link.Set(DeliverySystemLinkState.NotConfigured);
            logger.LogWarning("{Section}:BaseUrl is not set: this till will not receive captain or online orders.", DeliverySystemOptions.Section);
            return;
        }

        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(new Uri(settings.BaseUrl), settings.HubPath), http => http.Headers[DeliverySystemApi.NodeKeyHeader] = settings.NodeKey)
            .WithAutomaticReconnect(new ForeverRetryPolicy())
            .AddJsonProtocol(json => json.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();
        connection.KeepAliveInterval = KeepAliveInterval;
        connection.ServerTimeout = ServerTimeout;

        connection.On<Order>("OrderChanged", order => ApplyAsync(order, stoppingToken));
        connection.Reconnecting += error =>
        {
            link.Set(DeliverySystemLinkState.Offline);
            logger.LogWarning("Lost the connection to the delivery system; reconnecting. {Error}", error?.Message);
            return Task.CompletedTask;
        };
        connection.Reconnected += _ => AfterConnectedAsync(stoppingToken);

        // Only after automatic reconnection has given up, which ForeverRetryPolicy never does,
        // or on shutdown.
        connection.Closed += _ =>
        {
            link.Set(DeliverySystemLinkState.Offline);
            return Task.CompletedTask;
        };

        await ConnectAsync(connection, stoppingToken);
        await AfterConnectedAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(CatchUpInterval, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
            if (connection.State == HubConnectionState.Connected)
                await CatchUpAsync(stoppingToken);
        }
    }

    // Automatic reconnect only covers a connection that was once up. The first one can fail
    // too (the internet down when the machine boots), so it is retried here, for as long as it
    // takes.
    private async Task ConnectAsync(HubConnection connection, CancellationToken stoppingToken)
    {
        for (var attempt = 1; !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await connection.StartAsync(stoppingToken);
                return;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                link.Set(DeliverySystemLinkState.Offline);
                if (attempt == 1)
                    logger.LogWarning("Cannot reach the delivery system yet; working offline and retrying. {Error}", ex.Message);
                await Task.Delay(Backoff.After(attempt, TimeSpan.FromSeconds(30)), stoppingToken);
            }
        }
    }

    private async Task AfterConnectedAsync(CancellationToken cancellationToken)
    {
        link.Set(DeliverySystemLinkState.Online);
        logger.LogInformation("Connected to the delivery system");
        await CatchUpAsync(cancellationToken);
        await HurryPendingPushesAsync(cancellationToken);
    }

    // Pushes queued while offline go now, not whenever each one's backoff happens to end.
    private async Task HurryPendingPushesAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NodeDbContext>();
            var type = typeof(OrderChanged).FullName!;
            await db.OutboxMessages
                .Where(m => m.ProcessedAtUtc == null && m.Type == type)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.NextAttemptAtUtc, (DateTime?)null), cancellationToken);
            outboxSignal.Notify();
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not hurry the pending pushes; they will go on their own schedule");
        }
    }

    private async Task CatchUpAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NodeDbContext>();
            var api = scope.ServiceProvider.GetRequiredService<DeliverySystemApi>();

            var state = await db.SyncState.FindAsync([SyncState.OrdersChangedSince], cancellationToken);
            var since = state is null
                ? DateTime.UtcNow - FirstCatchUpWindow
                : DateTime.Parse(state.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var startedAt = DateTime.UtcNow;

            foreach (var order in await api.GetOrdersChangedSinceAsync(since, cancellationToken))
                await ApplyAsync(order, cancellationToken);

            // A minute of overlap: a change committed in the cloud just as this pull started, on
            // a clock not quite in step with this one, is picked up next time rather than missed.
            var next = (startedAt - TimeSpan.FromMinutes(1)).ToString("O", CultureInfo.InvariantCulture);
            if (state is null)
                db.SyncState.Add(new SyncState { Key = SyncState.OrdersChangedSince, Value = next });
            else
                state.Value = next;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Catching up on orders from the delivery system failed; will try again. {Error}", ex.Message);
        }
    }

    private async Task ApplyAsync(Order order, CancellationToken cancellationToken)
    {
        await _applying.WaitAsync(cancellationToken);
        try
        {
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ReceiveCloudOrderCommand(order), cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Left for the next catch-up, which will bring the order again.
            logger.LogError(ex, "Could not apply order {OrderPublicId} from the delivery system", order.PublicId);
        }
        finally
        {
            _applying.Release();
        }
    }

    // SignalR's default policy gives up after four attempts, about 40 seconds. A till must keep
    // trying for as long as the outage lasts.
    private sealed class ForeverRetryPolicy : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
            Backoff.After((int)Math.Min(retryContext.PreviousRetryCount, 20), TimeSpan.FromSeconds(30));
    }
}
