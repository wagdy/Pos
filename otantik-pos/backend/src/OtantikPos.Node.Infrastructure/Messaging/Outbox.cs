using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OtantikPos.Node.Infrastructure.Persistence;
using OtantikPos.Ordering.Application.Ports;
using OtantikPos.Ordering.Contracts;

namespace OtantikPos.Node.Infrastructure.Messaging;

// An Ordering event waiting to be delivered, saved in the same transaction as the order change
// that raised it.
public sealed class OutboxMessage
{
    // The event's own EventId, so a redelivery is recognisably the same event.
    public Guid Id { get; init; }

    // Assigned by the database. Messages are delivered in this order, except that one waiting
    // out a retry lets the ones behind it go first (see OutboxProcessor).
    public long Sequence { get; private set; }

    public string Type { get; init; } = string.Empty;

    public string Payload { get; init; } = string.Empty;

    public DateTime OccurredAtUtc { get; init; }

    public DateTime? ProcessedAtUtc { get; set; }

    public int Attempts { get; set; }

    public DateTime? NextAttemptAtUtc { get; set; }

    public string? LastError { get; set; }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages", NodeDbContext.MessagingSchema);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.Sequence).UseIdentityAlwaysColumn();
        builder.Property(m => m.Type).HasMaxLength(300);
        builder.Property(m => m.Payload).HasColumnType("jsonb");
        builder.HasIndex(m => m.Sequence).HasFilter("\"ProcessedAtUtc\" IS NULL");
    }
}

public sealed class EventSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    // Looked up by full name among the contract's own types, never Type.GetType: an
    // assembly-qualified name carries a version, and messages written before an upgrade would
    // stop resolving after it.
    private static readonly IReadOnlyDictionary<string, Type> EventTypes = typeof(OrderingEvent).Assembly
        .GetTypes()
        .Where(t => t is { IsAbstract: false } && t.IsSubclassOf(typeof(OrderingEvent)))
        .ToDictionary(t => t.FullName!);

    public OutboxMessage ToMessage(OrderingEvent orderingEvent) => new()
    {
        Id = orderingEvent.EventId,
        Type = orderingEvent.GetType().FullName!,
        Payload = JsonSerializer.Serialize(orderingEvent, orderingEvent.GetType(), Options),
        OccurredAtUtc = orderingEvent.OccurredAt,
    };

    public OrderingEvent FromMessage(OutboxMessage message)
    {
        if (!EventTypes.TryGetValue(message.Type, out var type))
            throw new InvalidOperationException($"Outbox message {message.Id} has unknown event type '{message.Type}'.");

        return (OrderingEvent)(JsonSerializer.Deserialize(message.Payload, type, Options)
            ?? throw new InvalidOperationException($"Outbox message {message.Id} has an empty payload."));
    }
}

internal sealed class EventOutbox(NodeDbContext db, EventSerializer serializer) : IEventOutbox
{
    public void Add(OrderingEvent orderingEvent) => db.OutboxMessages.Add(serializer.ToMessage(orderingEvent));
}
