using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OtantikPos.Node.Infrastructure.Persistence;
using OtantikPos.Ordering.Application.Ports;

namespace OtantikPos.Node.Infrastructure.Printing;

// The cash a customer handed over for a bill paid in cash, for the receipt's "Cash received" and
// "Change" lines, a reprint's included. The till's own record: the order the delivery system
// gets is unchanged.
public sealed class CashReceived
{
    public Guid OrderPublicId { get; init; }
    public decimal Amount { get; init; }
    public DateTime RecordedAtUtc { get; init; }
}

internal sealed class CashReceivedConfiguration : IEntityTypeConfiguration<CashReceived>
{
    public void Configure(EntityTypeBuilder<CashReceived> builder)
    {
        builder.ToTable("CashReceived", NodeDbContext.PosSchema);
        builder.HasKey(c => c.OrderPublicId);
        builder.Property(c => c.Amount).HasPrecision(18, 2);
    }
}

// Added to the context, so it is saved in the same transaction as the payment.
internal sealed class CashReceivedRecord(NodeDbContext db) : ICashReceived
{
    public void Record(Guid orderPublicId, decimal amount) =>
        db.CashReceived.Add(new CashReceived { OrderPublicId = orderPublicId, Amount = amount, RecordedAtUtc = DateTime.UtcNow });
}
