using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OtantikPos.Node.Infrastructure.Common;
using OtantikPos.Node.Infrastructure.Persistence;

namespace OtantikPos.Node.Infrastructure.Messaging;

// Delivers saved Ordering events to their MediatR handlers after the change that raised them
// has committed: kitchen tickets, stock deductions, and pushing orders to the delivery system.
//
// At least once, not exactly once: a crash between a handler finishing and the message being
// marked processed means delivering it again, and every handler copes.
//
// A failing message waits out a backoff while the ones behind it go ahead. That is what keeps
// the till working offline: pushes to the cloud (OrderChanged) fail and retry until the
// internet returns, while kitchen tickets and stock deductions carry on.
//
// One instance per machine is assumed, which is this deployment: one API on the restaurant's
// node. Two would both deliver every message; the handlers would survive it.
public sealed class OutboxProcessor(
    IServiceScopeFactory scopeFactory,
    EventSerializer serializer,
    OutboxSignal signal,
    ILogger<OutboxProcessor> logger) : BackgroundService
{
    private const int BatchSize = 50;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delivered = 0;
            try
            {
                delivered = await DeliverDueAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Outbox delivery pass failed");
            }

            if (delivered < BatchSize)
                await signal.WaitAsync(PollInterval, stoppingToken);
        }
    }

    private async Task<int> DeliverDueAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodeDbContext>();
        var now = DateTime.UtcNow;

        var due = await db.OutboxMessages
            .AsNoTracking()
            .Where(m => m.ProcessedAtUtc == null && (m.NextAttemptAtUtc == null || m.NextAttemptAtUtc <= now))
            .OrderBy(m => m.Sequence)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var message in due)
            await DeliverAsync(db, message, cancellationToken);

        return due.Count;
    }

    private async Task DeliverAsync(NodeDbContext db, OutboxMessage message, CancellationToken cancellationToken)
    {
        try
        {
            // A fresh scope per message, so handlers get a clean DbContext of their own.
            using (var handlerScope = scopeFactory.CreateScope())
            {
                await handlerScope.ServiceProvider.GetRequiredService<IPublisher>()
                    .Publish(serializer.FromMessage(message), cancellationToken);
            }

            await db.OutboxMessages
                .Where(m => m.Id == message.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.ProcessedAtUtc, DateTime.UtcNow)
                    .SetProperty(m => m.LastError, (string?)null), cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            var attempts = message.Attempts + 1;

            // Expected while offline: a cloud push failing is routine, not an error.
            var level = attempts >= 5 && ex is not OtantikPos.Ordering.Application.Ports.DeliverySystemUnavailableException
                ? LogLevel.Error
                : LogLevel.Warning;
            // Offline, the reason is all there is to say; a stack trace on every retry would bury the
            // log for as long as the outage lasts. Anything else gets its stack trace.
            if (ex is OtantikPos.Ordering.Application.Ports.DeliverySystemUnavailableException)
                logger.Log(level, "Outbox message {MessageId} ({Type}) waits for the delivery system, attempt {Attempts}: {Reason}", message.Id, message.Type, attempts, ex.Message);
            else
                logger.Log(level, ex, "Outbox message {MessageId} ({Type}) failed on attempt {Attempts}", message.Id, message.Type, attempts);

            await db.OutboxMessages
                .Where(m => m.Id == message.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Attempts, attempts)
                    .SetProperty(m => m.NextAttemptAtUtc, DateTime.UtcNow + Backoff.After(attempts, MaxRetryDelay))
                    .SetProperty(m => m.LastError, ex.Message.Length <= 4000 ? ex.Message : ex.Message[..4000]), cancellationToken);
        }
    }
}
