namespace Otantik.BuildingBlocks;

// The exceptions use cases throw on purpose, one per status code the API answers with. Kept
// apart from DomainException (a business rule refused the change), so the API can tell "no
// such order" (404) from "not allowed" (403) from "someone got there first" (409).

public sealed class NotFoundException(string entityName, object id)
    : Exception($"{entityName} {id} was not found.");

// Allowed to call the endpoint, but not to do this to this record now: a captain voiding, a
// cashier refunding an order that was never paid. The message is shown to the user as it is.
public sealed class ForbiddenException(string message) : Exception(message);

// The save collided with another: someone changed the same record meanwhile, or a unique
// value already exists. Infrastructure translates the database's own exceptions into this.
public sealed class ConflictException(string message, Exception? innerException = null)
    : Exception(message, innerException);

// A request that may be run again from the start when its save collides with another: it is
// idempotent by an id of its own, and it reads everything it needs afresh. Stock entered by
// hand (a delivery, spoilage, a count posted) is: it can collide with the outbox taking a sale's
// stock from the same material, which the person entering it neither did nor can fix.
public interface IRetryOnConflict;
