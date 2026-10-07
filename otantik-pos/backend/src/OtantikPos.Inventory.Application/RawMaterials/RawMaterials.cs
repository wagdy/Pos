using MediatR;
using OtantikPos.Inventory.Application.Common;
using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Domain.RawMaterials;

namespace OtantikPos.Inventory.Application.RawMaterials;

// What anyone who may see stock sees, cashiers included. No costs: those are a manager's, through
// the costing screens.
public sealed record RawMaterialDto(
    Guid Id,
    string Name,
    UnitOfMeasure Unit,
    decimal QuantityOnHand,
    decimal ReorderLevel,
    bool NeedsReorder,
    string? Code,
    string? Category,
    string PurchaseUnit,
    decimal PurchaseUnitSize,
    decimal DefaultYieldPercent)
{
    public static RawMaterialDto From(RawMaterial material) => new(
        material.Id,
        material.Name,
        material.Unit,
        material.QuantityOnHand,
        material.ReorderLevel,
        material.NeedsReorder,
        material.Code,
        material.Category,
        material.PurchaseUnit,
        material.PurchaseUnitSize,
        material.DefaultYieldPercent);
}

// The optional parts describe it for costing. PurchaseUnit and PurchaseUnitSize go together:
// "kg" and 1000 for a material counted in grams. CostPerPurchaseUnit is the price to start from.
public sealed record CreateRawMaterialCommand(
    string Name,
    UnitOfMeasure Unit,
    decimal ReorderLevel = 0,
    string? Code = null,
    string? Category = null,
    string? PurchaseUnit = null,
    decimal? PurchaseUnitSize = null,
    decimal? DefaultYieldPercent = null,
    decimal? CostPerPurchaseUnit = null) : IRequest<RawMaterialDto>;

internal sealed class CreateRawMaterialCommandHandler(IRawMaterialRepository materials, IUnitOfWork unitOfWork)
    : IRequestHandler<CreateRawMaterialCommand, RawMaterialDto>
{
    public async Task<RawMaterialDto> Handle(CreateRawMaterialCommand request, CancellationToken cancellationToken)
    {
        var material = new RawMaterial(request.Name, request.Unit, request.ReorderLevel);
        UpdateRawMaterialCommandHandler.Describe(material, request.Code, request.Category, request.PurchaseUnit, request.PurchaseUnitSize, request.DefaultYieldPercent, request.CostPerPurchaseUnit);
        materials.Add(material);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return RawMaterialDto.From(material);
    }
}

// No Unit here on purpose. Every recipe quantity and every ledger entry for a material is in
// its unit, so changing it would silently rescale all of them.
// CostPerPurchaseUnit null leaves the average cost as it is; a value replaces it.
public sealed record UpdateRawMaterialCommand(
    Guid RawMaterialId,
    string Name,
    decimal ReorderLevel,
    string? Code = null,
    string? Category = null,
    string? PurchaseUnit = null,
    decimal? PurchaseUnitSize = null,
    decimal? DefaultYieldPercent = null,
    decimal? CostPerPurchaseUnit = null) : IRequest<RawMaterialDto>;

internal sealed class UpdateRawMaterialCommandHandler(IRawMaterialRepository materials, IUnitOfWork unitOfWork)
    : IRequestHandler<UpdateRawMaterialCommand, RawMaterialDto>
{
    public async Task<RawMaterialDto> Handle(UpdateRawMaterialCommand request, CancellationToken cancellationToken)
    {
        var material = await materials.GetRequiredAsync(request.RawMaterialId, cancellationToken);

        material.Rename(request.Name);
        material.SetReorderLevel(request.ReorderLevel);
        Describe(material, request.Code, request.Category, request.PurchaseUnit, request.PurchaseUnitSize, request.DefaultYieldPercent, request.CostPerPurchaseUnit);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return RawMaterialDto.From(material);
    }

    // Shared with creating one. The purchase unit first: the starting cost is per that unit.
    internal static void Describe(
        RawMaterial material, string? code, string? category, string? purchaseUnit, decimal? purchaseUnitSize, decimal? defaultYield, decimal? cost)
    {
        material.Describe(code, category);
        if (purchaseUnit is not null || purchaseUnitSize is not null)
            material.SetPurchaseUnit(purchaseUnit ?? material.PurchaseUnit, purchaseUnitSize ?? material.PurchaseUnitSize);
        if (defaultYield is { } yield)
            material.SetDefaultYield(yield);
        if (cost is { } costPerPurchaseUnit)
            material.SetCost(costPerPurchaseUnit);
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
