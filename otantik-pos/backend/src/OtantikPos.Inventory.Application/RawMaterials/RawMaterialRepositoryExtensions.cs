using OtantikPos.Inventory.Application.Common;
using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Domain.RawMaterials;

namespace OtantikPos.Inventory.Application.RawMaterials;

internal static class RawMaterialRepositoryExtensions
{
    // For requests that list materials, such as a delivery or a stock count. A material listed
    // twice is refused rather than summed. On a delivery note that is far more often a
    // double scan than two genuine lines, and summing would hide it.
    public static async Task<IReadOnlyDictionary<Guid, RawMaterial>> GetRequiredByIdsAsync(
        this IRawMaterialRepository materials, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
            throw new DomainException("List at least one raw material.");
        if (ids.Distinct().Count() != ids.Count)
            throw new DomainException("Each raw material can be listed only once.");

        var found = (await materials.GetByIdsAsync(ids, cancellationToken)).ToDictionary(m => m.Id);
        foreach (var id in ids)
        {
            if (!found.ContainsKey(id))
                throw new NotFoundException(nameof(RawMaterial), id);
        }

        return found;
    }
}
