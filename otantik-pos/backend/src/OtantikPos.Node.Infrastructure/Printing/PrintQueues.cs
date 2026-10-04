using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Otantik.SharedKernel.Orders;
using OtantikPos.Node.Infrastructure.Common;
using OtantikPos.Node.Infrastructure.Persistence;
using OtantikPos.Ordering.Application.Ports;

namespace OtantikPos.Node.Infrastructure.Printing;

// Both queues store a print job and return; PrintWorker does the printing. A printer that is
// off never fails an order: its jobs wait.

internal sealed class KitchenPrintQueue(NodeDbContext db, RestaurantClock clock, IOptions<PrintingOptions> options, PrintSignal signal)
    : IKitchenPrintQueue
{
    public async Task EnqueueAsync(KitchenTicket ticket, CancellationToken cancellationToken)
    {
        var printer = options.Value.Printers.GetValueOrDefault(PrintingOptions.Kitchen) ?? new PrinterOptions();
        var now = DateTime.UtcNow;
        var content = KitchenTicketRenderer.Render(ticket, clock.ToLocal(now), printer);

        // A raw insert, for two reasons. ON CONFLICT DO NOTHING makes a second enqueue of the
        // same TicketId (the same event delivered twice) a no-op in one atomic statement. And it
        // leaves the change tracker alone: this runs inside an outbox delivery, where a
        // SaveChanges would also flush whatever another handler had pending.
        await db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO messaging."PrintJobs" ("Id", "Printer", "Content", "CreatedAtUtc", "Attempts")
             VALUES ({ticket.TicketId}, {PrintingOptions.Kitchen}, {content}, {now}, 0)
             ON CONFLICT ("Id") DO NOTHING
             """,
            cancellationToken);

        signal.Notify();
    }
}

internal sealed class ReceiptPrintQueue(NodeDbContext db, RestaurantClock clock, IOptions<PrintingOptions> options, PrintSignal signal)
    : IReceiptPrintQueue
{
    // Added to the context rather than inserted directly, so it is saved in the same
    // transaction as the audit entry that records it (see PrintFinalReceiptHandler). Each call
    // is a new job: a reprint is a second receipt.
    public Task EnqueueAsync(Order order, string printedByUserId, CancellationToken cancellationToken)
    {
        var printer = options.Value.Printers.GetValueOrDefault(PrintingOptions.Receipt) ?? new PrinterOptions();
        var now = DateTime.UtcNow;

        db.PrintJobs.Add(new PrintJob
        {
            Id = Guid.NewGuid(),
            Printer = PrintingOptions.Receipt,
            Content = ReceiptRenderer.Render(order, clock.ToLocal(now), printer, options.Value),
            CreatedAtUtc = now,
        });

        signal.Notify();
        return Task.CompletedTask;
    }
}
