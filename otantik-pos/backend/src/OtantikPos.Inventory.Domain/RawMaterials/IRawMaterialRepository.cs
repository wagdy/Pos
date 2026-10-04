using Otantik.BuildingBlocks;

namespace OtantikPos.Inventory.Domain.RawMaterials;

public interface IRawMaterialRepository : IRepository<RawMaterial>
{
    Task<IReadOnlyList<RawMaterial>> GetAllAsync(CancellationToken cancellationToken = default);

    // One round trip for every material an order touches, rather than one per ingredient.
    Task<IReadOnlyList<RawMaterial>> GetByIdsAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RawMaterial>> GetNeedingReorderAsync(CancellationToken cancellationToken = default);
}
