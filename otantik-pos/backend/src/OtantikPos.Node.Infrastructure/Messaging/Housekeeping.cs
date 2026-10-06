using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OtantikPos.Node.Infrastructure.Persistence;

namespace OtantikPos.Node.Infrastructure.Messaging;

// Messages already sent to the delivery system and tickets already printed are kept a week, for
// looking into a recent problem, then cleared. Kept for good, every order's copies of itself
// and its tickets grew the database, and each daily backup with it, by several hundred MB a
// year at a busy restaurant, along with the customers' phone numbers in them. Nothing still
// waiting to be sent or printed is ever cleared, and nothing reads the old ones: the outbox and
// the printers only look at what is still to do.
public sealed class Housekeeping(IServiceScopeFactory scopeFactory, ILogger<Housekeeping> logger) : BackgroundService
{
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);
    private static readonly TimeSpan Every = TimeSpan.FromHours(6);

    // Not while the till is starting, which has enough to do.
    private static readonly TimeSpan FirstAfter = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(FirstAfter, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var (sent, printed) = await ClearAsync(scope.ServiceProvider.GetRequiredService<NodeDbContext>(), DateTime.UtcNow, stoppingToken);
                if (sent + printed > 0)
                    logger.LogInformation("Cleared {Sent} sent messages and {Printed} printed tickets older than {Days} days", sent, printed, KeepFor.Days);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Clearing old sent messages and printed tickets failed; it is tried again later");
            }

            await Task.Delay(Every, stoppingToken);
        }
    }

    public static async Task<(int Sent, int Printed)> ClearAsync(NodeDbContext db, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var before = nowUtc - KeepFor;
        var sent = await db.OutboxMessages
            .Where(m => m.ProcessedAtUtc != null && m.ProcessedAtUtc < before)
            .ExecuteDeleteAsync(cancellationToken);
        var printed = await db.PrintJobs
            .Where(j => j.PrintedAtUtc != null && j.PrintedAtUtc < before)
            .ExecuteDeleteAsync(cancellationToken);
        return (sent, printed);
    }
}
