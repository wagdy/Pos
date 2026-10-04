namespace Otantik.BuildingBlocks;

public abstract class Entity
{
    // Generated here, not by a database identity column, for two reasons. A domain event
    // raised before SaveChanges has to name the rows it is about, and an identity column has
    // no value yet at that point. And this node runs offline: whatever it creates may later
    // be merged with the cloud or another branch, where an int sequence would collide.
    public Guid Id { get; protected init; } = Guid.NewGuid();
}
