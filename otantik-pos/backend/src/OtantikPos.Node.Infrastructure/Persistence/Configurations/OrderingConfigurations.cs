using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Otantik.SharedKernel.Auditing;
using Otantik.SharedKernel.Orders;

namespace OtantikPos.Node.Infrastructure.Persistence.Configurations;

// The shared Order, mapped with the delivery system's own column sizes and enum-as-name
// storage, so anything one database holds fits the other.
//
// No foreign keys from orders to the menu or to user accounts. An order snapshots the names
// and prices it needs, and the menu copy here can lose an item the delivery system deleted
// without taking the orders that sold it along.
internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("Orders", NodeDbContext.PosSchema);

        // User accounts live in the delivery system; UserId is a plain column here.
        builder.Ignore(o => o.User);
        // Computed in C# from Type and the items.
        builder.Ignore(o => o.IsPickup);
        builder.Ignore(o => o.BilledItemsSubtotal);

        builder.Property(o => o.UserId).HasMaxLength(450);
        builder.Property(o => o.CustomerName).HasMaxLength(200).IsRequired();
        builder.Property(o => o.CustomerPhone).HasMaxLength(30).IsRequired();
        builder.Property(o => o.DeliveryAddress).HasMaxLength(500).IsRequired();
        builder.Property(o => o.Notes).HasMaxLength(1000);
        builder.Property(o => o.PromoCodeText).HasMaxLength(50);
        builder.Property(o => o.ExternalSource).HasMaxLength(50);
        builder.Property(o => o.ExternalOrderId).HasMaxLength(100);
        builder.Property(o => o.TableNumber).HasMaxLength(20);
        builder.Property(o => o.CreatedByUserId).HasMaxLength(450);
        builder.Property(o => o.ClosedByUserId).HasMaxLength(450);

        builder.Property(o => o.Status).IsEnumName();
        builder.Property(o => o.PaymentMethod).IsEnumName();
        builder.Property(o => o.PaymentStatus).IsEnumName();
        builder.Property(o => o.Type).IsEnumName();

        // The order's identity everywhere; the till looks orders up by nothing else.
        builder.HasIndex(o => o.PublicId).IsUnique();
        builder.HasIndex(o => o.Status);
        builder.HasIndex(o => o.CreatedAt);

        // Two tills on one order: see NodeDbContext.TouchOrdersWithChangedItems.
        builder.Property<uint>("Version").IsRowVersion();

        builder.HasMany(o => o.OrderItems)
            .WithOne(i => i.Order)
            .HasForeignKey(i => i.OrderId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> builder)
    {
        builder.ToTable("OrderItems", NodeDbContext.PosSchema);

        builder.Ignore(i => i.MenuItem);
        builder.Ignore(i => i.LineTotal);
        builder.Ignore(i => i.IsVoided);
        builder.Ignore(i => i.IsSentToKitchen);

        builder.Property(i => i.MenuItemName).HasMaxLength(200).IsRequired();
        builder.Property(i => i.VariantName).HasMaxLength(100);
        builder.Property(i => i.Notes).HasMaxLength(500);
        builder.Property(i => i.VoidType).IsEnumName();
        builder.Property(i => i.VoidReason).HasMaxLength(500);
        builder.Property(i => i.VoidedByUserId).HasMaxLength(450);

        builder.HasIndex(i => i.PublicId).IsUnique();
        builder.HasIndex(i => i.MenuItemId);

        builder.HasMany(i => i.AddOns)
            .WithOne(a => a.OrderItem)
            .HasForeignKey(a => a.OrderItemId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class OrderItemAddOnConfiguration : IEntityTypeConfiguration<OrderItemAddOn>
{
    public void Configure(EntityTypeBuilder<OrderItemAddOn> builder)
    {
        builder.ToTable("OrderItemAddOns", NodeDbContext.PosSchema);

        builder.Ignore(a => a.AddOn);
        builder.Property(a => a.Name).HasMaxLength(100).IsRequired();
    }
}

internal sealed class OrderAuditEntryConfiguration : IEntityTypeConfiguration<OrderAuditEntry>
{
    public void Configure(EntityTypeBuilder<OrderAuditEntry> builder)
    {
        builder.ToTable("OrderAudit", NodeDbContext.PosSchema);

        builder.Property(a => a.Action).IsEnumName();
        builder.Property(a => a.VoidType).IsEnumName();
        builder.Property(a => a.Role).IsEnumName();
        builder.Property(a => a.UserId).HasMaxLength(450).IsRequired();
        builder.Property(a => a.Reason).HasMaxLength(500);

        builder.HasIndex(a => a.PublicId).IsUnique();
        builder.HasIndex(a => a.OrderPublicId);
    }
}
