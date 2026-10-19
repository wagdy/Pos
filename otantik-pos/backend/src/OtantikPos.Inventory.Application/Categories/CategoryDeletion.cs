using System.Globalization;
using MediatR;
using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Application.Approvals;
using OtantikPos.Inventory.Application.Common;
using OtantikPos.Inventory.Domain.Approvals;
using OtantikPos.Inventory.Domain.Categories;
using OtantikPos.Inventory.Domain.Expenses;
using OtantikPos.Inventory.Domain.RawMaterials;

namespace OtantikPos.Inventory.Application.Categories;

// An expense item made a raw material: what it is counted in, and how it is bought (a bottle of
// 750 ml).
public sealed record StockUnit(Guid ExpenseItemId, UnitOfMeasure Unit, string PurchaseUnit, decimal PurchaseUnitSize);

// A category's deletion as asked: where its raw materials go and where its expense items go, each
// to a category of its own kind or, converted on the way, of the other (Units: what each expense
// item made a raw material is counted in and bought by); and what was in it then, so a manager
// approves what was asked and nothing put in it since.
public sealed record CategoryDeletion(
    Guid MaterialsTo,
    Guid ExpenseItemsTo,
    IReadOnlyList<StockUnit> Units,
    IReadOnlyList<Guid> Materials,
    IReadOnlyList<Guid> ExpenseItems,
    bool MaterialsBecomeExpenses = false);

// What making raw materials expenses restates: their invoice lines and what they cost, their
// ledger entries and their count lines.
public sealed record ExpenseRestatement(int InvoiceLines, decimal InvoiceTotal, int StockMovements, int CountLines);

// The records naming the items, read and restated where they are kept.
public interface IItemConversions
{
    // Why these materials cannot be made expenses, each said by name: in a recipe, a prep recipe
    // or a batch; sold or used through one; received with no invoice; on an invoice with no
    // price; a shared cost; on an invoice beside the expense item it would join (namesakes, by
    // material); or named by a request waiting for a manager.
    Task<IReadOnlyList<string>> ExpenseBlockersAsync(
        IReadOnlyList<RawMaterial> materials, IReadOnlyDictionary<Guid, Guid> namesakes, Guid? exceptRequestId, CancellationToken cancellationToken);

    Task<ExpenseRestatement> RestatementAsync(IReadOnlyList<Guid> materialIds, CancellationToken cancellationToken);

    // Requests waiting for a manager that name any of these records.
    Task<int> PendingNamingAsync(IReadOnlyCollection<Guid> ids, Guid? exceptRequestId, CancellationToken cancellationToken);

    // Saves the unit of work, then moves each material's invoice lines onto its expense item and
    // takes its ledger entries and count lines out: all in one transaction.
    Task RestateAndSaveAsync(IReadOnlyList<(Guid MaterialId, Guid ExpenseItemId)> pairs, CancellationToken cancellationToken);
}

// Deletes the category, its raw materials first moved to MaterialsTo and its expense items to
// ExpenseItemsTo: one holding anything is not deleted without somewhere for it to go, and the
// purchases go with it. Each kind moved to a category of its kind goes as it is, done at once for
// whoever keeps stock; moved to one of the other kind, it is converted on the way, and that is a
// manager's: at once for one, kept with the decisions; asked of one by anyone else, the category
// locked until then. RequestId is made by the till.
public sealed record DeletePurchaseCategoryCommand(
    Guid CategoryId,
    Guid? MaterialsTo = null,
    Guid? ExpenseItemsTo = null,
    IReadOnlyList<StockUnit>? Units = null,
    Guid? RequestId = null,
    string? Reason = null,
    string StaffId = "",
    string StaffName = "",
    bool CanApprove = true,
    // False for a plain move: one that would convert anything is refused.
    bool AllowConversion = true) : IRequest<CategoryDeletionResult>, IRetryOnConflict;

// Applied (Request: the decision kept, when it converted anything) or Requested.
public sealed record CategoryDeletionResult(ApprovalOutcome Outcome, ApprovalRequestDto? Request);

// What a deletion would do, checked as it will be when sent; nothing is saved.
public sealed record PreviewCategoryDeletionQuery(Guid CategoryId, Guid? MaterialsTo, Guid? ExpenseItemsTo, IReadOnlyList<StockUnit>? Units)
    : IRequest<CategoryDeletionPreview>;

// Converts: whether it is a manager's to decide.
public sealed record CategoryDeletionPreview(string Summary, IReadOnlyList<ApprovalChange> Changes, bool Converts);

internal sealed class PreviewCategoryDeletionHandler(IPurchaseCategoryRepository categories, CategoryDeleter deleter)
    : IRequestHandler<PreviewCategoryDeletionQuery, CategoryDeletionPreview>
{
    public async Task<CategoryDeletionPreview> Handle(PreviewCategoryDeletionQuery request, CancellationToken cancellationToken)
    {
        var category = await categories.GetRequiredAsync(request.CategoryId, cancellationToken);
        var plan = await deleter.PlanAsync(category, request.MaterialsTo, request.ExpenseItemsTo, request.Units ?? [], null, cancellationToken);
        return new CategoryDeletionPreview(plan.Summary, plan.Changes, plan.Converts);
    }
}

internal sealed class DeletePurchaseCategoryHandler(
    IPurchaseCategoryRepository categories,
    IApprovalRequestRepository requests,
    CategoryDeleter deleter,
    IUnitOfWork unitOfWork) : IRequestHandler<DeletePurchaseCategoryCommand, CategoryDeletionResult>
{
    public async Task<CategoryDeletionResult> Handle(DeletePurchaseCategoryCommand request, CancellationToken cancellationToken)
    {
        if (request.RequestId is { } sent && await requests.GetByIdAsync(sent, cancellationToken) is { } already)
            return new CategoryDeletionResult(ApprovalResultDto.Of(already).Outcome, ApprovalRequestDto.From(already));
        // Already gone: a retry of a delete that worked.
        if (await categories.GetByIdAsync(request.CategoryId, cancellationToken) is not { } category)
            return new CategoryDeletionResult(ApprovalOutcome.Applied, null);

        await requests.EnsureNotPendingAsync(category.Id, cancellationToken);
        var plan = await deleter.PlanAsync(category, request.MaterialsTo, request.ExpenseItemsTo, request.Units ?? [], null, cancellationToken);
        if (plan.Converts && !request.AllowConversion)
            throw new DomainException(
                $"Moving what is in {category.Name} there would convert it to the other kind. Delete it from the categories screen, which asks where each kind goes.");
        if (!plan.Converts)
        {
            await deleter.ApplyAsync(plan, request.StaffName, cancellationToken);
            return new CategoryDeletionResult(ApprovalOutcome.Applied, null);
        }

        var id = request.RequestId ?? Guid.NewGuid();
        var payload = ApprovalPayload.Write(plan.Deletion);
        if (!request.CanApprove)
        {
            var asked = ApprovalRequest.AskToChange(
                id, ApprovalModule.PurchaseCategory, ApprovalAction.Delete, category.Id, plan.Summary, plan.Changes, payload, request.Reason,
                request.StaffId, request.StaffName);
            requests.Add(asked);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return new CategoryDeletionResult(ApprovalOutcome.Requested, ApprovalRequestDto.From(asked));
        }

        var decided = ApprovalRequest.DecidedChange(
            id, ApprovalModule.PurchaseCategory, ApprovalAction.Delete, category.Id, plan.Summary, plan.Changes, payload, request.Reason,
            request.StaffId, request.StaffName);
        requests.Add(decided);
        await deleter.ApplyAsync(plan, request.StaffName, cancellationToken);
        return new CategoryDeletionResult(ApprovalOutcome.Applied, ApprovalRequestDto.From(decided));
    }
}

// Plans, describes and carries out a category's deletion: for a manager's own and for an
// approval alike, checked again on approval against what is in the category then.
internal sealed class CategoryDeleter(
    IPurchaseCategoryRepository categories,
    IRawMaterialRepository materials,
    IExpenseItemRepository expenses,
    IItemConversions conversions,
    IUnitOfWork unitOfWork)
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    internal sealed record Plan(
        PurchaseCategory Category,
        PurchaseCategory? MaterialsTarget,
        PurchaseCategory? ExpensesTarget,
        CategoryDeletion Deletion,
        IReadOnlyList<RawMaterial> Materials,
        IReadOnlyList<ExpenseItem> ExpenseItems,
        IReadOnlyDictionary<Guid, ExpenseItem> Namesakes,
        string Summary,
        IReadOnlyList<ApprovalChange> Changes)
    {
        // Its raw materials going to a general expenses category: made expense items.
        public bool MaterialsBecomeExpenses => Deletion.MaterialsBecomeExpenses;

        // Its expense items in use going to a raw materials category: made raw materials.
        public bool ExpenseItemsBecomeStock => Deletion.Units.Count > 0;

        public bool Converts => MaterialsBecomeExpenses || ExpenseItemsBecomeStock;
    }

    // Everything that would refuse it, refused now.
    public async Task<Plan> PlanAsync(
        PurchaseCategory category, Guid? materialsTo, Guid? expenseItemsTo, IReadOnlyList<StockUnit> units, Guid? exceptRequestId,
        CancellationToken cancellationToken)
    {
        if (category.IsGeneral)
            throw new DomainException($"{category.Name} stays: it is where anything not yet sorted goes. Rename it if you like.");

        var inMaterials = (await materials.GetAllAsync(cancellationToken)).Where(m => m.CategoryId == category.Id).ToList();
        var inExpenses = (await expenses.GetInCategoryAsync(category.Id, cancellationToken))
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var materialsTarget = inMaterials.Count > 0 ? await TargetAsync(category, materialsTo, "raw materials", cancellationToken) : null;
        var expensesTarget = inExpenses.Count > 0 ? await TargetAsync(category, expenseItemsTo, "expense items", cancellationToken) : null;

        var namesakes = new Dictionary<Guid, ExpenseItem>();
        if (materialsTarget?.Kind == CategoryKind.Expenses)
            await CheckExpensesAsync(category, inMaterials, namesakes, exceptRequestId, cancellationToken);
        // The ones in use are made raw materials; archived ones move as they are.
        var stockUnits = expensesTarget?.Kind == CategoryKind.RawMaterials
            ? await CheckStockAsync([.. inExpenses.Where(e => !e.IsArchived)], units, exceptRequestId, cancellationToken)
            : [];

        var deletion = new CategoryDeletion(
            materialsTarget?.Id ?? Guid.Empty, expensesTarget?.Id ?? Guid.Empty, stockUnits,
            [.. inMaterials.Select(m => m.Id).Order()], [.. inExpenses.Select(e => e.Id).Order()],
            inMaterials.Count > 0 && materialsTarget?.Kind == CategoryKind.Expenses);
        var plan = new Plan(category, materialsTarget, expensesTarget, deletion, inMaterials, inExpenses, namesakes, "", []);
        var (summary, changes) = await DescribeAsync(plan, cancellationToken);
        return plan with { Summary = summary, Changes = changes };
    }

    // On approval: planned again as it is now, and refused if what is in the category changed.
    public async Task ApplyApprovedAsync(Guid categoryId, CategoryDeletion asked, Guid requestId, string by, CancellationToken cancellationToken)
    {
        var category = await categories.GetByIdAsync(categoryId, cancellationToken)
            ?? throw new DomainException("That category has been deleted already: reject this request.");
        var plan = await PlanAsync(
            category, NoneIfEmpty(asked.MaterialsTo), NoneIfEmpty(asked.ExpenseItemsTo), asked.Units, requestId, cancellationToken);
        if (!plan.Deletion.Materials.SequenceEqual(asked.Materials) || !plan.Deletion.ExpenseItems.SequenceEqual(asked.ExpenseItems)
            || plan.Deletion.Units.Count != asked.Units.Count || plan.MaterialsBecomeExpenses != asked.MaterialsBecomeExpenses)
            throw new DomainException($"What is in {category.Name} has changed since it was asked: reject this request, and ask again.");
        await ApplyAsync(plan, by, cancellationToken);
    }

    private static Guid? NoneIfEmpty(Guid id) => id == Guid.Empty ? null : id;

    // Where something of the category goes: another category, which must be there.
    private async Task<PurchaseCategory> TargetAsync(PurchaseCategory category, Guid? to, string what, CancellationToken cancellationToken)
    {
        if (to is not { } id || id == Guid.Empty)
            throw new DomainException($"{category.Name} holds {what}: choose the category they move to.");
        if (id == category.Id)
            throw new DomainException($"Choose another category for its {what}.");
        return await categories.GetByIdAsync(id, cancellationToken)
            ?? throw new DomainException("That category is no longer on the list. Choose it again, or add it.");
    }

    // Done and saved: everything moved, converted as planned, and the category gone.
    public async Task ApplyAsync(Plan plan, string by, CancellationToken cancellationToken)
    {
        var units = plan.Deletion.Units.ToDictionary(u => u.ExpenseItemId);
        var pairs = new List<(Guid MaterialId, Guid ExpenseItemId)>();
        var target = plan.MaterialsTarget?.Id ?? Guid.Empty;
        foreach (var material in plan.Materials)
        {
            ExpenseItem expense;
            material.MoveTo(target);
            if (!plan.MaterialsBecomeExpenses)
                continue;

            // The expense item of its name, when there is one (read only to find it: loaded to be
            // changed); otherwise a new one, counted as it was bought.
            if (plan.Namesakes.TryGetValue(material.Id, out var namesake))
            {
                expense = await expenses.GetRequiredAsync(namesake.Id, cancellationToken);
                expense.Restore();
                expense.MoveTo(target);
            }
            else
            {
                expenses.Add(expense = new ExpenseItem(Guid.NewGuid(), material.Name, target, null, material.PurchaseUnit));
            }
            material.ConvertToExpense(by);
            pairs.Add((material.Id, expense.Id));
        }

        var expensesTarget = plan.ExpensesTarget?.Id ?? Guid.Empty;
        foreach (var item in plan.ExpenseItems)
        {
            item.MoveTo(expensesTarget);
            if (units.TryGetValue(item.Id, out var unit))
            {
                var material = new RawMaterial(item.Name, unit.Unit);
                material.SetPurchaseUnit(unit.PurchaseUnit, unit.PurchaseUnitSize);
                material.Describe(null, expensesTarget);
                materials.Add(material);
                item.Archive();
            }
        }

        categories.Remove(plan.Category);
        if (pairs.Count > 0)
            await conversions.RestateAndSaveAsync(pairs, cancellationToken);
        else
            await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private async Task CheckExpensesAsync(
        PurchaseCategory category, IReadOnlyList<RawMaterial> inMaterials, Dictionary<Guid, ExpenseItem> namesakes, Guid? exceptRequestId,
        CancellationToken cancellationToken)
    {
        if (inMaterials.FirstOrDefault(m => m.IsPrepItem) is { } prep)
            throw new DomainException($"{prep.Name} is a prep item, made in the kitchen: move it out of {category.Name} first.");
        if (inMaterials.GroupBy(m => ExpenseItem.Normalize(m.Name)).FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw new DomainException($"Two raw materials in {category.Name} are called {twice.First().Name}: rename one first.");
        foreach (var material in inMaterials)
            if (await expenses.GetByNormalizedNameAsync(ExpenseItem.Normalize(material.Name), cancellationToken) is { } namesake)
                namesakes[material.Id] = namesake;

        var blockers = await conversions.ExpenseBlockersAsync(
            inMaterials, namesakes.ToDictionary(n => n.Key, n => n.Value.Id), exceptRequestId, cancellationToken);
        if (blockers.Count > 0)
            throw new DomainException(
                $"Its raw materials cannot be made expenses: {string.Join(" ", blockers.Take(4))}" +
                (blockers.Count > 4 ? $" And {blockers.Count - 4} more." : "") +
                " Move them as they are instead, or put those right first.");
    }

    private async Task<List<StockUnit>> CheckStockAsync(
        IReadOnlyList<ExpenseItem> toStock, IReadOnlyList<StockUnit> units, Guid? exceptRequestId, CancellationToken cancellationToken)
    {
        var given = units.GroupBy(u => u.ExpenseItemId).ToDictionary(g => g.Key, g => g.First());
        var names = (await materials.GetAllAsync(cancellationToken)).Where(m => !m.IsArchived).Select(m => ExpenseItem.Normalize(m.Name)).ToHashSet();
        var checkedUnits = new List<StockUnit>();
        foreach (var item in toStock)
        {
            if (!given.TryGetValue(item.Id, out var unit) || !Enum.IsDefined(unit.Unit))
                throw new DomainException($"Say what {item.Name} is counted in, and what it is bought by.");
            if (string.IsNullOrWhiteSpace(unit.PurchaseUnit) || unit.PurchaseUnit.Trim().Length > 32)
                throw new DomainException($"Name what {item.Name} is bought by, such as kg or bottle, in under 32 characters.");
            if (unit.PurchaseUnitSize <= 0)
                throw new DomainException($"Say how much one {unit.PurchaseUnit.Trim()} of {item.Name} holds: more than nothing.");
            if (names.Contains(item.NormalizedName))
                throw new DomainException($"There is already a raw material called {item.Name}: rename one first.");
            checkedUnits.Add(unit with { PurchaseUnit = unit.PurchaseUnit.Trim() });
        }
        if (await conversions.PendingNamingAsync([.. toStock.Select(e => e.Id)], exceptRequestId, cancellationToken) > 0)
            throw new DomainException("A request waiting for a manager names one of its expense items: they can be converted once it is decided.");
        return checkedUnits;
    }

    // "Category منظفات deleted: 1 raw material becomes an expense item in صيانه", and line by line
    // what it does.
    private async Task<(string Summary, IReadOnlyList<ApprovalChange> Changes)> DescribeAsync(Plan plan, CancellationToken cancellationToken)
    {
        var changes = new List<ApprovalChange>();
        var converted = new List<string>();
        var (inMaterials, inExpenses) = (plan.Materials, plan.ExpenseItems);
        if (plan.MaterialsBecomeExpenses)
        {
            converted.Add($"{Count(inMaterials.Count, "raw material")} {(inMaterials.Count == 1 ? "becomes an expense item" : "become expense items")} in {plan.MaterialsTarget!.Name}");
            foreach (var m in inMaterials)
                changes.Add(new ApprovalChange(
                    m.Name, $"Raw material, {Amount(m.QuantityOnHand, m.Unit)} on hand",
                    plan.Namesakes.TryGetValue(m.Id, out var joins) ? $"Expense item (joins {joins.Name})" : "Expense item"));
            var restated = await conversions.RestatementAsync([.. inMaterials.Select(m => m.Id)], cancellationToken);
            changes.Add(new ApprovalChange(
                "Invoice lines", null,
                restated.InvoiceLines == 0
                    ? "None: never bought on an invoice"
                    : $"{Count(restated.InvoiceLines, "line")} ({Money(restated.InvoiceTotal)}) restated as expenses, each on its invoice's date"));
            string[] ledger =
            [
                .. restated.StockMovements > 0 ? [Count(restated.StockMovements, "entry", "entries")] : Array.Empty<string>(),
                .. restated.CountLines > 0 ? [Count(restated.CountLines, "count line")] : Array.Empty<string>(),
            ];
            changes.Add(new ApprovalChange("Stock ledger", null, ledger.Length == 0 ? "Nothing in it" : $"{string.Join(" and ", ledger)} taken out"));
            changes.Add(new ApprovalChange("Their expenses count as", null, Line(plan.MaterialsTarget.ExpenseLine)));
        }
        if (plan.ExpenseItemsBecomeStock)
        {
            var made = plan.Deletion.Units.Count;
            converted.Add($"{Count(made, "expense item")} {(made == 1 ? "becomes a raw material" : "become raw materials")} in {plan.ExpensesTarget!.Name}");
            foreach (var unit in plan.Deletion.Units)
                changes.Add(new ApprovalChange(
                    inExpenses.Single(e => e.Id == unit.ExpenseItemId).Name, "Expense item",
                    $"Raw material counted in {Unit(unit.Unit)}, bought by the {unit.PurchaseUnit} of {Amount(unit.PurchaseUnitSize, unit.Unit)}"));
            changes.Add(new ApprovalChange("Past expense lines", null, "Stay expenses; each starts with nothing on hand"));
        }

        // The rest goes as it is.
        var moved = new List<string>();
        if (inMaterials.Count > 0 && !plan.MaterialsBecomeExpenses)
            moved.Add($"{Count(inMaterials.Count, "raw material")} to {plan.MaterialsTarget!.Name}");
        var expensesAsTheyAre = inExpenses.Count - plan.Deletion.Units.Count;
        if (expensesAsTheyAre > 0)
            moved.Add($"{Count(expensesAsTheyAre, "expense item")} to {plan.ExpensesTarget!.Name}");
        if (moved.Count > 0)
            changes.Add(new ApprovalChange("Moved as they are", null, string.Join("; ", moved)));

        var summary = converted.Count > 0
            ? $"Category {plan.Category.Name} deleted: {string.Join("; ", converted)}"
            : moved.Count > 0 ? $"Category {plan.Category.Name} deleted, what is in it moved" : $"Category {plan.Category.Name} deleted";
        return (summary, changes);
    }

    private static string Count(int n, string what, string? plural = null) => n == 1 ? $"1 {what}" : $"{n} {plural ?? what + "s"}";

    private static string Unit(UnitOfMeasure unit) => unit switch
    {
        UnitOfMeasure.Gram => "g",
        UnitOfMeasure.Millilitre => "ml",
        _ => "pieces",
    };

    private static string Line(ExpenseLine line) => line switch
    {
        ExpenseLine.Occupation => "Occupation costs",
        ExpenseLine.OtherFixed => "Other fixed costs",
        ExpenseLine.Wages => "Wages and benefits",
        _ => "Controllable costs",
    };

    // "12 pieces", "1 piece", "2,400 g".
    private static string Amount(decimal value, UnitOfMeasure unit) =>
        $"{Quantity(value)} {(Quantity(value) == "1" && unit == UnitOfMeasure.Piece ? "piece" : Unit(unit))}";

    private static string Quantity(decimal value) => Math.Round(value, 3, MidpointRounding.AwayFromZero).ToString("#,0.###", Invariant);

    private static string Money(decimal value) => $"L.E {value.ToString("#,0.00", Invariant)}";
}
