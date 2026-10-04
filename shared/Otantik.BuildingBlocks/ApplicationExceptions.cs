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
