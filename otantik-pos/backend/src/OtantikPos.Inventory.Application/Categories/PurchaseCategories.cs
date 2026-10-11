using MediatR;
using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Application.Common;
using OtantikPos.Inventory.Application.RawMaterials;
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

internal sealed class SavePurchaseCategoryHandler(IPurchaseCategoryRepository categories, IUnitOfWork unitOfWork)
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
            category.Update(request.Name, request.NameAr, request.ExpenseLine);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return PurchaseCategoryDto.From(category, (await categories.GetUseAsync(cancellationToken)).GetValueOrDefault(category.Id));
    }
}

// Deletes the category. What is filed under it moves first to MoveToId: one with materials or
// expense items is not deleted without somewhere for them to go. Their purchases go with them.
public sealed record DeletePurchaseCategoryCommand(Guid CategoryId, Guid? MoveToId = null) : IRequest;

internal sealed class DeletePurchaseCategoryHandler(
    IPurchaseCategoryRepository categories,
    IRawMaterialRepository materials,
    IExpenseItemRepository expenses,
    IUnitOfWork unitOfWork) : IRequestHandler<DeletePurchaseCategoryCommand>
{
    public async Task Handle(DeletePurchaseCategoryCommand request, CancellationToken cancellationToken)
    {
        // Already gone: a retry of a delete that worked.
        if (await categories.GetByIdAsync(request.CategoryId, cancellationToken) is not { } category)
            return;
        if (category.IsGeneral)
            throw new DomainException($"{category.Name} stays: it is where anything not yet sorted goes. Rename it if you like.");

        var inMaterials = (await materials.GetAllAsync(cancellationToken)).Where(m => m.CategoryId == category.Id).ToList();
        var inExpenses = await expenses.GetInCategoryAsync(category.Id, cancellationToken);
        var filed = inMaterials.Count + inExpenses.Count;
        if (request.MoveToId is { } moveToId)
        {
            if (moveToId == category.Id)
                throw new DomainException("Choose another category for what is in it.");
            _ = await categories.GetRequiredAsync(moveToId, cancellationToken);
            foreach (var material in inMaterials)
                material.MoveTo(moveToId);
            foreach (var expense in inExpenses)
                expense.MoveTo(moveToId);
        }
        else if (filed > 0)
        {
            throw new DomainException(
                $"{category.Name} has {filed} {(filed == 1 ? "item" : "items")} in it. Choose the category {(filed == 1 ? "it moves" : "they move")} to.");
        }

        categories.Remove(category);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
