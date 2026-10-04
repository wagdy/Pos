namespace Otantik.BuildingBlocks;

// Deliberately not MediatR's INotification. Infrastructure wraps each event when it
// publishes, so the dispatcher can be swapped without touching an entity.
//
// The setters are init rather than get-only so that an event read back from the outbox keeps
// the EventId it was written with. A get-only property would be regenerated on
// deserialisation, and handlers could no longer tell a redelivery from a new event.
public abstract record DomainEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();

    public DateTime OccurredAtUtc { get; init; } = DateTime.UtcNow;
}
