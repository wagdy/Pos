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

// Misplaced: items in use of the kind it is not, filed there before the kinds were kept apart.
public sealed record PurchaseCategoryDto(
    Guid Id, string Name, string? NameAr, ExpenseLine ExpenseLine, bool IsGeneral, int Materials, int ExpenseItems, CategoryKind Kind, int Misplaced)
{
    public static PurchaseCategoryDto From(PurchaseCategory category, PurchaseCategoryUse? use) => new(
        category.Id, category.Name, category.NameAr, category.ExpenseLine, category.IsGeneral, use?.Materials ?? 0, use?.ExpenseItems ?? 0,
        category.Kind, use?.Misplaced(category.Kind) ?? 0);
}

public static class PurchaseCategoryReading
{
    // The category asked for something of that kind, which must exist and be of its kind; none
    // asked for is the General of its kind, made if it is missing. Current: the one it is filed
    // under now, which it may stay in though it is of the other kind (filed before the kinds were
    // kept apart); moved, it goes to one of its own.
    public static async Task<PurchaseCategory> ResolveAsync(
        this IPurchaseCategoryRepository categories, Guid? categoryId, CategoryKind kind, Guid? current, CancellationToken cancellationToken)
    {
        var defaultId = PurchaseCategory.DefaultOf(kind);
        if (categoryId is { } id && id != defaultId)
        {
            var category = await categories.GetByIdAsync(id, cancellationToken)
                ?? throw new DomainException("That category is no longer on the list. Choose it again, or add it.");
            if (category.Kind != kind && id != current)
                throw new DomainException(kind == CategoryKind.RawMaterials
                    ? $"{category.Name} is a general expenses category: choose a raw materials category."
                    : $"{category.Name} is a raw materials category: choose a general expenses category.");
            return category;
        }

        var general = await categories.GetByIdAsync(defaultId, cancellationToken);
        if (general is null)
            categories.Add(general = kind == CategoryKind.Expenses ? PurchaseCategory.GeneralExpenses() : PurchaseCategory.General());
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

// By name, the Generals first.
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
// Kind: what it files; a new one is raw materials unless said, and one already there keeps its
// own unless said (and changes it only while it holds nothing of the other kind).
public sealed record SavePurchaseCategoryCommand(
    Guid CategoryId, string Name, string? NameAr = null, ExpenseLine ExpenseLine = ExpenseLine.Controllable, CategoryKind? Kind = null)
    : IRequest<PurchaseCategoryDto>;

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
            categories.Add(category = new PurchaseCategory(
                request.CategoryId, request.Name, request.NameAr, request.ExpenseLine, request.Kind ?? CategoryKind.RawMaterials));
        else
        {
            // Locked while its deletion waits for a manager.
            await requests.EnsureNotPendingAsync(category.Id, cancellationToken);
            category.Update(request.Name, request.NameAr, request.ExpenseLine);
            if (request.Kind is { } kind && kind != category.Kind)
            {
                var use = (await categories.GetUseAsync(cancellationToken)).GetValueOrDefault(category.Id);
                category.ChangeKind(kind, use?.Misplaced(kind) ?? 0);
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return PurchaseCategoryDto.From(category, (await categories.GetUseAsync(cancellationToken)).GetValueOrDefault(category.Id));
    }
}
