using Otantik.BuildingBlocks;

namespace OtantikPos.Inventory.Application.Common;

public static class RepositoryExtensions
{
    public static async Task<TAggregate> GetRequiredAsync<TAggregate>(
        this IRepository<TAggregate> repository, Guid id, CancellationToken cancellationToken)
        where TAggregate : AggregateRoot =>
        await repository.GetByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException(typeof(TAggregate).Name, id);
}
