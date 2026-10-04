namespace Otantik.BuildingBlocks;

public interface IUnitOfWork
{
    // Saves every change made in the current use case in one transaction, together with the
    // domain events raised by the aggregates involved. That pairing is the contract the
    // outbox relies on: an order can never be saved as completed without its stock deduction
    // also being saved for delivery.
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
