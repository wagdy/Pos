using MediatR;
using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Application.Categories;
using OtantikPos.Inventory.Application.Common;
using OtantikPos.Inventory.Domain.Categories;
using OtantikPos.Inventory.Domain.Expenses;
using OtantikPos.Inventory.Domain.Purchases;

namespace OtantikPos.Inventory.Application.Expenses;

// The expense items purchase invoices can carry beside raw materials: never stock, only a cost
// under a category. ExpenseItemId is made by the till.

public sealed record ExpenseItemDto(
    Guid Id,
    string Name,
    string? NameAr,
    Guid CategoryId,
    string Category,
    string? CategoryAr,
    string? Unit,
    bool IsArchived,
    // Invoices it is on, drafts included: any at all and deleting it archives it instead.
    int Invoices,
    decimal PostedTotal,
    DateTime? LastPostedAtUtc)
{
    public static ExpenseItemDto From(ExpenseItem item, PurchaseCategory? category, ExpenseItemUse? use) => new(
        item.Id, item.Name, item.NameAr, item.CategoryId, category?.Name ?? PurchaseCategory.GeneralName, category?.NameAr, item.Unit,
        item.IsArchived, use?.Invoices ?? 0, use?.PostedTotal ?? 0, use?.LastPostedAtUtc);
}

public enum ExpenseItemRemoval
{
    Deleted = 1,

    // It is on invoices: kept for them, off the list for new ones.
    Archived = 2,
}

public sealed record ExpenseItemRemovalDto(ExpenseItemRemoval Outcome);

internal static class ExpenseItemReading
{
    public static async Task<ExpenseItemDto> DtoAsync(
        this IPurchaseCategoryRepository categories, IPurchaseInvoiceRepository invoices, ExpenseItem item, CancellationToken cancellationToken) =>
        ExpenseItemDto.From(
            item, await categories.GetByIdAsync(item.CategoryId, cancellationToken),
            (await invoices.GetExpenseItemUseAsync(cancellationToken)).GetValueOrDefault(item.Id));
}

// By name. The archived ones only when asked for: the Expense items screen shows them to restore.
public sealed record GetExpenseItemsQuery(bool IncludeArchived = false) : IRequest<IReadOnlyList<ExpenseItemDto>>;

internal sealed class GetExpenseItemsHandler(IExpenseItemRepository items, IPurchaseCategoryRepository categories, IPurchaseInvoiceRepository invoices)
    : IRequestHandler<GetExpenseItemsQuery, IReadOnlyList<ExpenseItemDto>>
{
    public async Task<IReadOnlyList<ExpenseItemDto>> Handle(GetExpenseItemsQuery request, CancellationToken cancellationToken)
    {
        var byId = (await categories.GetAllAsync(cancellationToken)).ToDictionary(c => c.Id);
        var use = await invoices.GetExpenseItemUseAsync(cancellationToken);
        return (await items.GetAllAsync(cancellationToken))
            .Where(i => request.IncludeArchived || !i.IsArchived)
            .OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(i => ExpenseItemDto.From(i, byId.GetValueOrDefault(i.CategoryId), use.GetValueOrDefault(i.Id)))
            .ToList();
    }
}

// Adds the expense item, or changes it. No category: General. Two cannot share a name, archived
// or not: the invoices would not say which was which.
public sealed record SaveExpenseItemCommand(Guid ExpenseItemId, string Name, Guid? CategoryId = null, string? NameAr = null, string? Unit = null)
    : IRequest<ExpenseItemDto>;

internal sealed class SaveExpenseItemHandler(
    IExpenseItemRepository items, IPurchaseCategoryRepository categories, IPurchaseInvoiceRepository invoices, IUnitOfWork unitOfWork)
    : IRequestHandler<SaveExpenseItemCommand, ExpenseItemDto>
{
    public async Task<ExpenseItemDto> Handle(SaveExpenseItemCommand request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.Name)
            && await items.GetByNormalizedNameAsync(ExpenseItem.Normalize(request.Name), cancellationToken) is { } namesake
            && namesake.Id != request.ExpenseItemId)
        {
            throw new DomainException(namesake.IsArchived
                ? $"{namesake.Name} is already an expense item, archived. Restore it from the archived ones."
                : $"{namesake.Name} is already an expense item.");
        }

        var category = await categories.ResolveAsync(request.CategoryId, cancellationToken);
        var item = await items.GetByIdAsync(request.ExpenseItemId, cancellationToken);
        if (item is null)
            items.Add(item = new ExpenseItem(request.ExpenseItemId, request.Name ?? string.Empty, category.Id, request.NameAr, request.Unit));
        else
            item.Update(request.Name ?? string.Empty, category.Id, request.NameAr, request.Unit);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await categories.DtoAsync(invoices, item, cancellationToken);
    }
}

// Deleted if it is on no invoice; archived if it is, so the invoices keep it.
public sealed record DeleteExpenseItemCommand(Guid ExpenseItemId) : IRequest<ExpenseItemRemovalDto>;

internal sealed class DeleteExpenseItemHandler(IExpenseItemRepository items, IPurchaseInvoiceRepository invoices, IUnitOfWork unitOfWork)
    : IRequestHandler<DeleteExpenseItemCommand, ExpenseItemRemovalDto>
{
    public async Task<ExpenseItemRemovalDto> Handle(DeleteExpenseItemCommand request, CancellationToken cancellationToken)
    {
        // Already gone: a retry of a delete that worked.
        if (await items.GetByIdAsync(request.ExpenseItemId, cancellationToken) is not { } item)
            return new ExpenseItemRemovalDto(ExpenseItemRemoval.Deleted);

        var outcome = ExpenseItemRemoval.Deleted;
        if (await invoices.AnyForExpenseItemAsync(item.Id, cancellationToken))
        {
            item.Archive();
            outcome = ExpenseItemRemoval.Archived;
        }
        else
        {
            items.Remove(item);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new ExpenseItemRemovalDto(outcome);
    }
}

// Back on the list new invoices pick from.
public sealed record RestoreExpenseItemCommand(Guid ExpenseItemId) : IRequest<ExpenseItemDto>;

internal sealed class RestoreExpenseItemHandler(
    IExpenseItemRepository items, IPurchaseCategoryRepository categories, IPurchaseInvoiceRepository invoices, IUnitOfWork unitOfWork)
    : IRequestHandler<RestoreExpenseItemCommand, ExpenseItemDto>
{
    public async Task<ExpenseItemDto> Handle(RestoreExpenseItemCommand request, CancellationToken cancellationToken)
    {
        var item = await items.GetRequiredAsync(request.ExpenseItemId, cancellationToken);
        item.Restore();
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await categories.DtoAsync(invoices, item, cancellationToken);
    }
}
