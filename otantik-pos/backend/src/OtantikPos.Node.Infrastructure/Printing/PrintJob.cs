using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OtantikPos.Node.Infrastructure.Persistence;

namespace OtantikPos.Node.Infrastructure.Printing;

// A ticket rendered and waiting for its printer. Stored as the bytes the printer receives, so
// what prints is exactly what was rendered when the ticket was queued, even after a restart.
public sealed class PrintJob
{
    // The KitchenTicket's TicketId, which is the id of the event that produced it. The primary
    // key is what makes enqueueing the same ticket twice a no-op.
    public Guid Id { get; init; }

    public long Sequence { get; private set; }

    // Which configured printer this goes to: "Kitchen" or "Receipt" (PrintingOptions). A name,
    // not a fixed list, so a bar or grill printer can be added without a migration.
    public string Printer { get; init; } = string.Empty;

    public byte[] Content { get; init; } = [];

    public DateTime CreatedAtUtc { get; init; }

    public DateTime? PrintedAtUtc { get; set; }

    public int Attempts { get; set; }

    public DateTime? NextAttemptAtUtc { get; set; }

    public string? LastError { get; set; }
}

internal sealed class PrintJobConfiguration : IEntityTypeConfiguration<PrintJob>
{
    public void Configure(EntityTypeBuilder<PrintJob> builder)
    {
        builder.ToTable("PrintJobs", NodeDbContext.MessagingSchema);

        builder.Property(j => j.Id).ValueGeneratedNever();
        builder.Property(j => j.Sequence).UseIdentityAlwaysColumn();
        builder.Property(j => j.Printer).HasMaxLength(50);
        builder.Property(j => j.LastError).HasMaxLength(1000);

        builder.HasIndex(j => new { j.Printer, j.Sequence }).HasFilter("\"PrintedAtUtc\" IS NULL");
    }
}
