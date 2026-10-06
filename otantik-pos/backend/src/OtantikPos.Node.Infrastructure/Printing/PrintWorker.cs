using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OtantikPos.Node.Infrastructure.Common;
using OtantikPos.Node.Infrastructure.Persistence;

namespace OtantikPos.Node.Infrastructure.Printing;

// Sends queued print jobs to their printers, strictly in order per printer: if a ticket fails,
// nothing behind it for that printer goes until it does. Out-of-order tickets confuse a
// kitchen more than late ones, and a void slip before the order it cancels makes no sense.
public sealed class PrintWorker(
    IServiceScopeFactory scopeFactory,
    TcpPrinterClient printerClient,
    IOptionsMonitor<PrintingOptions> options,
    PrintSignal signal,
    PrinterStatus status,
    ILogger<PrintWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    // Short, because the usual fault is a printer switched off or out of paper, and the
    // ticket should come out within seconds of someone fixing it.
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    // How often to repeat a problem that has not changed. The worker checks every 5 seconds;
    // logging each time buried everything else in the log under copies of one line.
    private static readonly TimeSpan Reminder = TimeSpan.FromMinutes(10);

    // Per printer: the problem last logged and when. Logged again only when it changes, or
    // as a reminder.
    private readonly Dictionary<string, (string Problem, DateTime LoggedAt)> _problems = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PrintDueAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Print pass failed");
            }

            await signal.WaitAsync(PollInterval, stoppingToken);
        }
    }

    private async Task PrintDueAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodeDbContext>();

        var pending = await db.PrintJobs
            .Where(j => j.PrintedAtUtc == null)
            .OrderBy(j => j.Sequence)
            .ToListAsync(cancellationToken);

        foreach (var queue in pending.GroupBy(j => j.Printer))
        {
            if (!options.CurrentValue.Printers.TryGetValue(queue.Key, out var printer) || string.IsNullOrWhiteSpace(printer.Host))
            {
                // The count is left out of the problem's text on purpose: one more ticket in the
                // queue is not a new problem.
                Report(queue.Key, "no Host configured", $"{queue.Count()} job(s) waiting for printer {queue.Key}, which has no Host configured");
                continue;
            }

            foreach (var job in queue)
            {
                // The head of this printer's queue is waiting out a retry, so everything
                // behind it waits too.
                if (job.NextAttemptAtUtc > DateTime.UtcNow)
                    break;

                try
                {
                    await printerClient.SendAsync(printer, job.Content, cancellationToken);
                    job.PrintedAtUtc = DateTime.UtcNow;
                    job.LastError = null;
                    Recovered(queue.Key);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    job.Attempts++;
                    job.NextAttemptAtUtc = DateTime.UtcNow + Backoff.After(job.Attempts, MaxRetryDelay);
                    job.LastError = ex.Message.Length <= 1000 ? ex.Message : ex.Message[..1000];
                    Report(queue.Key, ex.Message, $"Printer {queue.Key} at {printer.Host}:{printer.Port} is failing: {ex.Message}");
                    break;
                }
                finally
                {
                    // Saved after every job, not once at the end. A crash halfway through a
                    // batch must not reprint the tickets that already came out.
                    await db.SaveChangesAsync(cancellationToken);
                }
            }
        }

        // What the tills show: each printer still holding tickets after this pass because the
        // one at the head of its queue failed, or because it has nowhere to print.
        status.Set(pending
            .Where(j => j.PrintedAtUtc == null)
            .GroupBy(j => j.Printer)
            .Select(queue => (Queue: queue, Head: queue.First()))
            .Where(x => x.Head.LastError is not null || !IsConfigured(x.Queue.Key))
            .Select(x => new PrinterProblem(x.Queue.Key, x.Head.LastError ?? "no printer address is set", x.Queue.Count(), x.Head.CreatedAtUtc))
            .OrderBy(p => p.Printer)
            .ToList());
    }

    private bool IsConfigured(string printer) =>
        options.CurrentValue.Printers.TryGetValue(printer, out var settings) && !string.IsNullOrWhiteSpace(settings.Host);

    private void Report(string printer, string problem, string message)
    {
        var now = DateTime.UtcNow;
        if (_problems.TryGetValue(printer, out var last) && last.Problem == problem && now - last.LoggedAt < Reminder)
            return;

        _problems[printer] = (problem, now);
        logger.LogWarning("{Message}", message);
    }

    private void Recovered(string printer)
    {
        if (_problems.Remove(printer))
            logger.LogInformation("Printer {Printer} is printing again", printer);
    }
}
