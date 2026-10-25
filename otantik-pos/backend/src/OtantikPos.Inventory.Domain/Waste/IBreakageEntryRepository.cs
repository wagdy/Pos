using Otantik.BuildingBlocks;

namespace OtantikPos.Inventory.Domain.Waste;

public interface IBreakageEntryRepository : IRepository<BreakageEntry>
{
    // The business days from `from` to `to`, voided ones too.
    Task<IReadOnlyList<BreakageEntry>> GetBetweenAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    Task<BreakageEntry?> GetForTransactionAsync(Guid assetTransactionId, CancellationToken cancellationToken = default);
}
