namespace Otantik.BuildingBlocks;

// A business rule refused the call. Messages are written to be shown at the till as they
// are; the API maps this type to a 4xx, never a 500.
public sealed class DomainException(string message) : Exception(message);
