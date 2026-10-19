using MediatR;
using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Application.Approvals;
using OtantikPos.Inventory.Application.Common;
using OtantikPos.Inventory.Application.RawMaterials;
using OtantikPos.Inventory.Domain.Approvals;
using OtantikPos.Inventory.Domain.Categories;
using OtantikPos.Inventory.Domain.Expenses;
using OtantikPos.Inventory.Domain.RawMaterials;

namespace OtantikPos.Inventory.Application.Categories;

// The purchase categories raw materials and expense items are filed under. CategoryId is made by
// the till.

public sealed record PurchaseCategoryDto(
    Guid Id, string Name, string? NameAr, ExpenseLine ExpenseLine, bool IsGeneral, int Materials, int ExpenseItems)
{
    public static PurchaseCategoryDto From(PurchaseCategory category, PurchaseCategoryUse? use) => new(
        category.Id, category.Name, category.NameAr, category.ExpenseLine, category.IsGeneral, use?.Materials ?? 0, use?.ExpenseItems ?? 0);
}

public static class PurchaseCategoryReading
{
    // The category asked for, which must exist; none asked for is General, made if it is missing.
    public static async Task<PurchaseCategory> ResolveAsync(this IPurchaseCategoryRepository categories, Guid? categoryId, CancellationToken cancellationToken)
    {
        if (categoryId is { } id && id != PurchaseCategory.GeneralId)
            return await categories.GetByIdAsync(id, cancellationToken)
                ?? throw new DomainException("That category is no longer on the list. Choose it again, or add it.");

        var general = await categories.GetByIdAsync(PurchaseCategory.GeneralId, cancellationToken);
        if (general is null)
            categories.Add(general = PurchaseCategory.General());
        return general;
    }

    // Materials as the stock screens show them, each with its category's name.
    public static async Task<IReadOnlyList<RawMaterialDto>> DtosAsync(
        this IPurchaseCategoryRepository categories, IEnumerable<RawMaterial> materials, CancellationToken cancellationToken)
    {
        var names = (await categories.GetAllAsync(cancellationToken)).ToDictionary(c => c.Id);
        return materials.Select(m => RawMaterialDto.From(m, names.GetValueOrDefault(m.CategoryId))).ToList();
    }

    public static async Task<RawMaterialDto> DtoAsync(this IPurchaseCategoryRepository categories, RawMaterial material, CancellationToken cancellationToken) =>
        (await categories.DtosAsync([material], cancellationToken))[0];
}

// By name, General first.
public sealed record GetPurchaseCategoriesQuery : IRequest<IReadOnlyList<PurchaseCategoryDto>>;

internal sealed class GetPurchaseCategoriesHandler(IPurchaseCategoryRepository categories)
    : IRequestHandler<GetPurchaseCategoriesQuery, IReadOnlyList<PurchaseCategoryDto>>
{
    public async Task<IReadOnlyList<PurchaseCategoryDto>> Handle(GetPurchaseCategoriesQuery request, CancellationToken cancellationToken)
    {
        var use = await categories.GetUseAsync(cancellationToken);
        return (await categories.GetAllAsync(cancellationToken))
            .OrderByDescending(c => c.IsGeneral)
            .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => PurchaseCategoryDto.From(c, use.GetValueOrDefault(c.Id)))
            .ToList();
    }
}

// Adds the category, or changes it. Two cannot share a name: the reports could not tell them apart.
public sealed record SavePurchaseCategoryCommand(
    Guid CategoryId, string Name, string? NameAr = null, ExpenseLine ExpenseLine = ExpenseLine.Controllable) : IRequest<PurchaseCategoryDto>;

internal sealed class SavePurchaseCategoryHandler(IPurchaseCategoryRepository categories, IApprovalRequestRepository requests, IUnitOfWork unitOfWork)
    : IRequestHandler<SavePurchaseCategoryCommand, PurchaseCategoryDto>
{
    public async Task<PurchaseCategoryDto> Handle(SavePurchaseCategoryCommand request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.Name)
            && await categories.GetByNormalizedNameAsync(PurchaseCategory.Normalize(request.Name), cancellationToken) is { } namesake
            && namesake.Id != request.CategoryId)
            throw new DomainException($"There is already a category called {namesake.Name}.");

        var category = await categories.GetByIdAsync(request.CategoryId, cancellationToken);
        if (category is null)
            categories.Add(category = new PurchaseCategory(request.CategoryId, request.Name, request.NameAr, request.ExpenseLine));
        else
        {
            // Locked while its deletion waits for a manager.
            await requests.EnsureNotPendingAsync(category.Id, cancellationToken);
            category.Update(request.Name, request.NameAr, request.ExpenseLine);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return PurchaseCategoryDto.From(category, (await categories.GetUseAsync(cancellationToken)).GetValueOrDefault(category.Id));
    }
}
