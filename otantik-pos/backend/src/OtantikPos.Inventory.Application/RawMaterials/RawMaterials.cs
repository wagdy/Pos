using MediatR;
using OtantikPos.Inventory.Application.Common;
using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Domain.RawMaterials;

namespace OtantikPos.Inventory.Application.RawMaterials;

public sealed record RawMaterialDto(
    Guid Id,
    string Name,
    UnitOfMeasure Unit,
    decimal QuantityOnHand,
    decimal ReorderLevel,
    bool NeedsReorder)
{
    public static RawMaterialDto From(RawMaterial material) => new(
        material.Id,
        material.Name,
        material.Unit,
        material.QuantityOnHand,
        material.ReorderLevel,
        material.NeedsReorder);
}

public sealed record CreateRawMaterialCommand(string Name, UnitOfMeasure Unit, decimal ReorderLevel = 0)
    : IRequest<RawMaterialDto>;

internal sealed class CreateRawMaterialCommandHandler(IRawMaterialRepository materials, IUnitOfWork unitOfWork)
    : IRequestHandler<CreateRawMaterialCommand, RawMaterialDto>
{
    public async Task<RawMaterialDto> Handle(CreateRawMaterialCommand request, CancellationToken cancellationToken)
    {
        var material = new RawMaterial(request.Name, request.Unit, request.ReorderLevel);
        materials.Add(material);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return RawMaterialDto.From(material);
    }
}

// No Unit here on purpose. Every recipe quantity and every ledger entry for a material is in
// its unit, so changing it would silently rescale all of them.
public sealed record UpdateRawMaterialCommand(Guid RawMaterialId, string Name, decimal ReorderLevel)
    : IRequest<RawMaterialDto>;

internal sealed class UpdateRawMaterialCommandHandler(IRawMaterialRepository materials, IUnitOfWork unitOfWork)
    : IRequestHandler<UpdateRawMaterialCommand, RawMaterialDto>
{
    public async Task<RawMaterialDto> Handle(UpdateRawMaterialCommand request, CancellationToken cancellationToken)
    {
        var material = await materials.GetRequiredAsync(request.RawMaterialId, cancellationToken);

        material.Rename(request.Name);
        material.SetReorderLevel(request.ReorderLevel);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return RawMaterialDto.From(material);
    }
}

public sealed record GetRawMaterialsQuery(bool NeedsReorderOnly = false) : IRequest<IReadOnlyList<RawMaterialDto>>;

internal sealed class GetRawMaterialsQueryHandler(IRawMaterialRepository materials)
    : IRequestHandler<GetRawMaterialsQuery, IReadOnlyList<RawMaterialDto>>
{
    public async Task<IReadOnlyList<RawMaterialDto>> Handle(GetRawMaterialsQuery request, CancellationToken cancellationToken)
    {
        var found = request.NeedsReorderOnly
            ? await materials.GetNeedingReorderAsync(cancellationToken)
            : await materials.GetAllAsync(cancellationToken);

        return found.Select(RawMaterialDto.From).ToList();
    }
}
