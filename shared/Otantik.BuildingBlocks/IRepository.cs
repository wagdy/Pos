namespace Otantik.BuildingBlocks;

// Constrained to aggregate roots on purpose. There is no OrderItem repository: an item is
// loaded, changed and saved through its Order, which is what keeps the order's totals and
// status consistent with its items.
//
// No Remove here either. Orders, customers and stock movements are never deleted; the
// repositories that do allow it declare it themselves.
public interface IRepository<TAggregate> where TAggregate : AggregateRoot
{
    Task<TAggregate?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    void Add(TAggregate aggregate);
}
