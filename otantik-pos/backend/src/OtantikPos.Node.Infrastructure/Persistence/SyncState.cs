using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace OtantikPos.Node.Infrastructure.Persistence;

// Small named values the sync with the delivery system needs to survive a restart, such as
// how far order catch-up has got.
public sealed class SyncState
{
    public const string OrdersChangedSince = "orders.changed-since";
    public const string ReferenceDataSyncedAt = "reference-data.synced-at";

    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

internal sealed class SyncStateConfiguration : IEntityTypeConfiguration<SyncState>
{
    public void Configure(EntityTypeBuilder<SyncState> builder)
    {
        builder.ToTable("SyncState", NodeDbContext.MessagingSchema);
        builder.HasKey(s => s.Key);
        builder.Property(s => s.Key).HasMaxLength(100);
        builder.Property(s => s.Value).HasMaxLength(500);
    }
}
