using System.Globalization;
using MediatR;
using Otantik.BuildingBlocks;
using OtantikPos.Hr.Domain;
using OtantikPos.Hr.Domain.Custody;
using OtantikPos.Hr.Domain.Employees;
using OtantikPos.Inventory.Application.Approvals;
using OtantikPos.Inventory.Domain.Approvals;
using OtantikPos.Inventory.Domain.Expenses;
using OtantikPos.Inventory.Domain.Waste;

namespace OtantikPos.Inventory.Application.Custody;

// Custody & Assets (عهدة وأصول): the categories, the assets and whose custody each is in, and the
// vouchers that move them. Categories are a manager's. Assets and vouchers are entered by whoever
// manages HR: a manager's at once, anyone else's asked of a manager (approval module Custody),
// and nothing changes until then. A consumable's breakage is routed to Costing → Waste as a
// breakage entry, on the voucher's business day.

public sealed record AssetCategoryDto(Guid Id, string Name, bool IsConsumable, int Assets, int InCustody);

public sealed record AssetItemDto(
    Guid Id,
    string Name,
    Guid? CategoryId,
    string? CategoryName,
    bool IsConsumable,
    decimal? UnitCost,
    int Quantity,
    decimal? TotalCost,
    Guid? EmployeeId,
    string? EmployeeName,
    AssetOrigin Origin,
    AssetStatus Status,
    Guid? SourceInvoiceId,
    string? Notes,
    string CreatedBy,
    DateTime CreatedAtUtc);

public sealed record AssetTransactionDto(
    Guid Id,
    Guid AssetItemId,
    string AssetName,
    AssetTransactionType Type,
    DateOnly Date,
    Guid? EmployeeId,
    string? EmployeeName,
    int Quantity,
    AssetMovementReason? Reason,
    string? Notes,
    decimal? UnitCost,
    decimal? Cost,
    string RecordedBy,
    DateTime RecordedAtUtc,
    // The breakage entry it made in Costing → Waste, a consumable's breakage.
    Guid? WasteEntryId,
    bool RoutedToWaste);

public sealed record AssetDetailDto(AssetItemDto Asset, IReadOnlyList<AssetTransactionDto> Vouchers);

// What is in an employee's custody, and since when it is theirs.
public sealed record CustodyItemDto(AssetItemDto Asset, DateOnly? Since);

public sealed record BreakageEntryDto(
    Guid Id,
    DateOnly BusinessDate,
    Guid AssetItemId,
    Guid AssetTransactionId,
    string Description,
    int Quantity,
    decimal? UnitCost,
    decimal Cost,
    Guid? EmployeeId,
    string? EmployeeName,
    string? Notes,
    string RecordedBy,
    DateTime RecordedAtUtc,
    bool IsVoided);

// What a request holds: an asset's details (a new one, or a change), or a voucher for it.
// TransactionId is the till's: a retry finds the voucher made.
public sealed record AssetInput(string Name, Guid? CategoryId, decimal? UnitCost, int Quantity, string? Notes);

public sealed record CheckInInput(Guid TransactionId, Guid EmployeeId, DateOnly Date, string? Notes);

public sealed record CheckOutInput(Guid TransactionId, AssetMovementReason Reason, int Quantity, DateOnly Date, string? Notes);

public sealed record CustodyRequest(AssetInput? Asset = null, CheckInInput? CheckIn = null, CheckOutInput? CheckOut = null);

public sealed record CustodyResultDto(ApprovalOutcome Outcome, AssetItemDto? Asset, AssetTransactionDto? Voucher, ApprovalRequestDto? Request);

// The names the lists show, read once.
internal sealed class CustodyNames(IReadOnlyDictionary<Guid, AssetCategory> categories, IReadOnlyDictionary<Guid, string> employees)
{
    public static async Task<CustodyNames> ReadAsync(IAssetCategoryRepository categories, IEmployeeRepository employees, CancellationToken cancellationToken) =>
        new((await categories.GetAllAsync(cancellationToken)).ToDictionary(c => c.Id),
            (await employees.GetAllAsync(cancellationToken)).ToDictionary(e => e.Id, e => e.Name));

    public AssetItemDto Asset(AssetItem a)
    {
        var category = a.CategoryId is { } id ? categories.GetValueOrDefault(id) : null;
        return new AssetItemDto(
            a.Id, a.Name, a.CategoryId, category?.Name, category?.IsConsumable ?? false, a.UnitCost, a.Quantity, a.TotalCost, a.EmployeeId, Employee(a.EmployeeId),
            a.Origin, a.Status, a.SourceInvoiceId, a.Notes, a.CreatedBy, a.CreatedAtUtc);
    }

    public AssetTransactionDto Voucher(AssetTransaction t, string assetName) => new(
        t.Id, t.AssetItemId, assetName, t.Type, t.Date, t.EmployeeId, Employee(t.EmployeeId), t.Quantity, t.Reason, t.Notes, t.UnitCost, t.Cost,
        t.RecordedBy, t.RecordedAtUtc, t.WasteEntryId, t.RoutedToWaste);

    public BreakageEntryDto Breakage(BreakageEntry b) => new(
        b.Id, b.BusinessDate, b.AssetItemId, b.AssetTransactionId, b.Description, b.Quantity, b.UnitCost, b.Cost, b.EmployeeId, Employee(b.EmployeeId),
        b.Notes, b.RecordedBy, b.RecordedAtUtc, b.IsVoided);

    public string? Employee(Guid? id) => id is { } e ? employees.GetValueOrDefault(e) : null;

    public AssetCategory? Category(Guid? id) => id is { } c ? categories.GetValueOrDefault(c) : null;
}

// ---- Categories: a manager's ----

public sealed record GetAssetCategoriesQuery : IRequest<IReadOnlyList<AssetCategoryDto>>;

internal sealed class GetAssetCategoriesHandler(IAssetCategoryRepository categories, IAssetItemRepository assets)
    : IRequestHandler<GetAssetCategoriesQuery, IReadOnlyList<AssetCategoryDto>>
{
    public async Task<IReadOnlyList<AssetCategoryDto>> Handle(GetAssetCategoriesQuery request, CancellationToken cancellationToken)
    {
        var all = await assets.GetAllAsync(null, cancellationToken);
        return (await categories.GetAllAsync(cancellationToken))
            .Select(c => new AssetCategoryDto(
                c.Id, c.Name, c.IsConsumable, all.Count(a => a.CategoryId == c.Id && !a.IsGone),
                all.Count(a => a.CategoryId == c.Id && a.Status == AssetStatus.InCustody)))
            .ToList();
    }
}

// Added under this id, or renamed and marked consumable or not. Two are not named alike.
public sealed record SaveAssetCategoryCommand(Guid CategoryId, string Name, bool IsConsumable) : IRequest<AssetCategoryDto>, IRetryOnConflict;

internal sealed class SaveAssetCategoryHandler(IAssetCategoryRepository categories, IAssetItemRepository assets, IUnitOfWork unitOfWork)
    : IRequestHandler<SaveAssetCategoryCommand, AssetCategoryDto>
{
    public async Task<AssetCategoryDto> Handle(SaveAssetCategoryCommand request, CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if ((await categories.GetAllAsync(cancellationToken))
            .FirstOrDefault(c => c.Id != request.CategoryId && string.Equals(c.Name, name, StringComparison.CurrentCultureIgnoreCase)) is { } same)
            throw new DomainException($"There is a {same.Name} category already.");

        var category = await categories.GetByIdAsync(request.CategoryId, cancellationToken);
        if (category is null)
            categories.Add(category = new AssetCategory(request.CategoryId, name, request.IsConsumable));
        else
        {
            category.Rename(name);
            category.SetConsumable(request.IsConsumable);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        var all = (await assets.GetAllAsync(null, cancellationToken)).Where(a => a.CategoryId == category.Id).ToList();
        return new AssetCategoryDto(category.Id, category.Name, category.IsConsumable, all.Count(a => !a.IsGone), all.Count(a => a.Status == AssetStatus.InCustody));
    }
}

// Deleted only when nothing is in it, nor bought as it.
public sealed record DeleteAssetCategoryCommand(Guid CategoryId) : IRequest, IRetryOnConflict;

internal sealed class DeleteAssetCategoryHandler(
    IAssetCategoryRepository categories, IAssetItemRepository assets, IExpenseItemRepository expenses, IUnitOfWork unitOfWork)
    : IRequestHandler<DeleteAssetCategoryCommand>
{
    public async Task Handle(DeleteAssetCategoryCommand request, CancellationToken cancellationToken)
    {
        if (await categories.GetByIdAsync(request.CategoryId, cancellationToken) is not { } category)
            return;
        if (await assets.AnyInCategoryAsync(category.Id, cancellationToken))
            throw new DomainException($"Assets are in {category.Name}, those gone too: move them to another category first.");
        if ((await expenses.GetAllAsync(cancellationToken)).FirstOrDefault(e => e.AssetCategoryId == category.Id) is { } bought)
            throw new DomainException($"{bought.Name} is bought as {category.Name}: change that expense first.");
        categories.Remove(category);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

// ---- Assets ----

// Entered in HR only (Link with Financials off), or with those bought on invoices too (on).
public sealed record GetAssetsQuery(bool WithFinancial) : IRequest<IReadOnlyList<AssetItemDto>>;

internal sealed class GetAssetsHandler(IAssetItemRepository assets, IAssetCategoryRepository categories, IEmployeeRepository employees)
    : IRequestHandler<GetAssetsQuery, IReadOnlyList<AssetItemDto>>
{
    public async Task<IReadOnlyList<AssetItemDto>> Handle(GetAssetsQuery request, CancellationToken cancellationToken)
    {
        var names = await CustodyNames.ReadAsync(categories, employees, cancellationToken);
        return (await assets.GetAllAsync(request.WithFinancial ? null : AssetOrigin.HrManual, cancellationToken)).Select(names.Asset).ToList();
    }
}

public sealed record GetAssetQuery(Guid AssetId) : IRequest<AssetDetailDto?>;

internal sealed class GetAssetHandler(
    IAssetItemRepository assets, IAssetTransactionRepository vouchers, IAssetCategoryRepository categories, IEmployeeRepository employees)
    : IRequestHandler<GetAssetQuery, AssetDetailDto?>
{
    public async Task<AssetDetailDto?> Handle(GetAssetQuery request, CancellationToken cancellationToken)
    {
        if (await assets.GetByIdAsync(request.AssetId, cancellationToken) is not { } asset)
            return null;
        var names = await CustodyNames.ReadAsync(categories, employees, cancellationToken);
        return new AssetDetailDto(
            names.Asset(asset), (await vouchers.GetForAssetAsync(asset.Id, cancellationToken)).Select(v => names.Voucher(v, asset.Name)).ToList());
    }
}

// What is in an employee's custody now, with what it cost: their profile's, for whoever sees HR.
// The portal has its own, without the costs (GetPortalCustodyQuery).
public sealed record GetEmployeeCustodyQuery(Guid EmployeeId) : IRequest<IReadOnlyList<CustodyItemDto>>;

internal sealed class GetEmployeeCustodyHandler(
    IAssetItemRepository assets, IAssetTransactionRepository vouchers, IAssetCategoryRepository categories, IEmployeeRepository employees)
    : IRequestHandler<GetEmployeeCustodyQuery, IReadOnlyList<CustodyItemDto>>
{
    public async Task<IReadOnlyList<CustodyItemDto>> Handle(GetEmployeeCustodyQuery request, CancellationToken cancellationToken)
    {
        var names = await CustodyNames.ReadAsync(categories, employees, cancellationToken);
        var held = new List<CustodyItemDto>();
        foreach (var asset in await assets.GetInCustodyOfAsync(request.EmployeeId, cancellationToken))
        {
            var since = (await vouchers.GetForAssetAsync(asset.Id, cancellationToken))
                .FirstOrDefault(v => v.Type == AssetTransactionType.CheckIn && v.EmployeeId == request.EmployeeId)?.Date;
            held.Add(new CustodyItemDto(names.Asset(asset), since));
        }

        return held;
    }
}

// The vouchers of the business days from From to To, the latest first.
public sealed record GetAssetTransactionsQuery(DateOnly From, DateOnly To) : IRequest<IReadOnlyList<AssetTransactionDto>>;

internal sealed class GetAssetTransactionsHandler(
    IAssetItemRepository assets, IAssetTransactionRepository vouchers, IAssetCategoryRepository categories, IEmployeeRepository employees)
    : IRequestHandler<GetAssetTransactionsQuery, IReadOnlyList<AssetTransactionDto>>
{
    public async Task<IReadOnlyList<AssetTransactionDto>> Handle(GetAssetTransactionsQuery request, CancellationToken cancellationToken)
    {
        if (request.To < request.From)
            throw new DomainException("The last day is on or after the first.");
        var names = await CustodyNames.ReadAsync(categories, employees, cancellationToken);
        var assetNames = (await assets.GetAllAsync(null, cancellationToken)).ToDictionary(a => a.Id, a => a.Name);
        return (await vouchers.GetBetweenAsync(request.From, request.To, cancellationToken))
            .Select(v => names.Voucher(v, assetNames.GetValueOrDefault(v.AssetItemId) ?? "—"))
            .ToList();
    }
}

// Costing → Waste's breakage: the business days from From to To, voided entries too.
public sealed record GetBreakageQuery(DateOnly From, DateOnly To) : IRequest<IReadOnlyList<BreakageEntryDto>>;

internal sealed class GetBreakageHandler(IBreakageEntryRepository breakage, IAssetCategoryRepository categories, IEmployeeRepository employees)
    : IRequestHandler<GetBreakageQuery, IReadOnlyList<BreakageEntryDto>>
{
    public async Task<IReadOnlyList<BreakageEntryDto>> Handle(GetBreakageQuery request, CancellationToken cancellationToken)
    {
        var names = await CustodyNames.ReadAsync(categories, employees, cancellationToken);
        return (await breakage.GetBetweenAsync(request.From, request.To, cancellationToken)).Select(names.Breakage).ToList();
    }
}

// Checks, describes and makes an asset's change or a voucher: for a manager's own and for an
// approval alike. What it makes is added for the caller to save.
internal sealed class CustodyWriter(
    IAssetItemRepository assets,
    IAssetCategoryRepository categories,
    IAssetTransactionRepository vouchers,
    IEmployeeRepository employees,
    IBreakageEntryRepository breakage,
    IRecordDescriptions clock)
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    // Refused before a request goes to a manager, and checked again on approval.
    public async Task CheckAsync(Guid assetId, CustodyRequest request, CancellationToken cancellationToken)
    {
        var existing = await assets.GetByIdAsync(assetId, cancellationToken);
        if (request.Asset is { } a)
        {
            // The domain's own checks: a draft that is never saved.
            // The quantity of a new one; an existing one's is the vouchers', checked below.
            _ = new AssetItem(
                assetId, a.Name, a.CategoryId, a.UnitCost, existing is null ? a.Quantity : Math.Max(existing.Quantity, 1), AssetOrigin.HrManual, "check");
            if (a.CategoryId is { } categoryId && await categories.GetByIdAsync(categoryId, cancellationToken) is null)
                throw new DomainException("That category is not there any more: choose another.");
            if (existing is not null && a.Quantity != existing.Quantity)
                throw new DomainException($"How many there are changes by vouchers: there are {existing.Quantity:N0}.");
            if (existing is { Origin: AssetOrigin.FinancialPurchase } && a.UnitCost != existing.UnitCost)
                throw new DomainException("An asset bought on an invoice costs what its invoice says.");
            return;
        }

        var asset = existing ?? throw new NotFoundException(nameof(AssetItem), assetId);
        if (request.CheckIn is { } checkIn)
        {
            CheckDate(checkIn.Date);
            var employee = await employees.GetByIdAsync(checkIn.EmployeeId, cancellationToken) ?? throw new NotFoundException(nameof(Employee), checkIn.EmployeeId);
            if (!employee.IsEmployedOn(checkIn.Date))
                throw new DomainException($"{employee.Name} is not employed on {Day(checkIn.Date)}.");
            if (asset.CheckInRefusal(employee.Id) is { } why)
                throw new DomainException(why);
        }
        else if (request.CheckOut is { } checkOut)
        {
            CheckDate(checkOut.Date);
            if (asset.CheckOutRefusal(checkOut.Reason, checkOut.Quantity) is { } why)
                throw new DomainException(why);
        }
        else
        {
            throw new DomainException("Say what changes.");
        }
    }

    // "New asset · Dinner plates × 40 · L.E 35.00 a piece", "Into Hassan's custody · Dinner
    // plates × 40", "Out of Hassan's custody, broken · Dinner plates × 3", and what changes.
    public async Task<(string Summary, ApprovalAction Action, IReadOnlyList<ApprovalChange> Changes)> DescribeAsync(
        Guid assetId, CustodyRequest request, CancellationToken cancellationToken)
    {
        var existing = await assets.GetByIdAsync(assetId, cancellationToken);
        var names = await CustodyNames.ReadAsync(categories, employees, cancellationToken);
        if (request.Asset is { } a)
        {
            var lines = new List<ApprovalChange>();
            void Line(string label, string? before, string after)
            {
                if (existing is null || before != after)
                    lines.Add(new ApprovalChange(label, existing is null ? null : before, after));
            }

            Line("Name", existing?.Name, a.Name.Trim());
            Line("Category", names.Category(existing?.CategoryId)?.Name ?? "—", names.Category(a.CategoryId)?.Name ?? "—");
            Line("Cost a piece", Cost(existing?.UnitCost), Cost(a.UnitCost));
            if (existing is null)
                Line("How many", null, a.Quantity.ToString("N0", Invariant));
            Line("Notes", existing?.Notes ?? "—", string.IsNullOrWhiteSpace(a.Notes) ? "—" : a.Notes.Trim());
            return existing is null
                ? ($"New asset · {a.Name.Trim()} × {a.Quantity:N0} · {Cost(a.UnitCost)} a piece", ApprovalAction.Create, lines)
                : ($"Asset changed · {existing.Name}", ApprovalAction.Update, lines);
        }

        var asset = existing ?? throw new NotFoundException(nameof(AssetItem), assetId);
        string Holder(Guid? id, AssetStatus status) => names.Employee(id) is { } who ? $"{who}'s custody" : status == AssetStatus.Maintenance ? "Maintenance" : "The store";
        if (request.CheckIn is { } checkIn)
        {
            var who = names.Employee(checkIn.EmployeeId) ?? "an employee";
            return ($"Into {who}'s custody · {asset.Name} × {asset.Quantity:N0}", ApprovalAction.Update,
                [new ApprovalChange("Custody", Holder(asset.EmployeeId, asset.Status), $"{who}'s custody"), new ApprovalChange("Date", null, Day(checkIn.Date))]);
        }

        var checkOut = request.CheckOut ?? throw new DomainException("Say what changes.");
        var from = Holder(asset.EmployeeId, asset.Status);
        var reason = ReasonOf(checkOut.Reason);
        var left = asset.Quantity - checkOut.Quantity;
        IReadOnlyList<ApprovalChange> changes = checkOut.Reason switch
        {
            AssetMovementReason.Returned => [new ApprovalChange("Custody", from, "The store")],
            AssetMovementReason.Maintenance => [new ApprovalChange("Custody", from, "Maintenance")],
            _ => [new ApprovalChange("How many", asset.Quantity.ToString("N0", Invariant), left.ToString("N0", Invariant)), new ApprovalChange("Why", null, reason)],
        };
        return ($"Out of {from}, {reason.ToLowerInvariant()} · {asset.Name} × {checkOut.Quantity:N0}", ApprovalAction.Update,
            [.. changes, new ApprovalChange("Date", null, Day(checkOut.Date))]);
    }

    // The asset made or changed, and the voucher a check-in or check-out made.
    public async Task<(AssetItem Asset, AssetTransaction? Voucher)> ApplyAsync(
        Guid assetId, CustodyRequest request, string recordedBy, CancellationToken cancellationToken)
    {
        await CheckAsync(assetId, request, cancellationToken);
        var asset = await assets.GetByIdAsync(assetId, cancellationToken);
        if (request.Asset is { } a)
        {
            if (asset is null)
            {
                asset = new AssetItem(assetId, a.Name, a.CategoryId, a.UnitCost, a.Quantity, AssetOrigin.HrManual, recordedBy, notes: a.Notes);
                assets.Add(asset);
            }
            else
            {
                asset.Rename(a.Name);
                asset.MoveTo(a.CategoryId);
                asset.SetUnitCost(a.UnitCost);
                asset.SetNotes(a.Notes);
            }

            return (asset, null);
        }

        if (asset is null)
            throw new NotFoundException(nameof(AssetItem), assetId);
        if (request.CheckIn is { } checkIn)
        {
            var voucher = asset.CheckIn(checkIn.TransactionId, checkIn.EmployeeId, checkIn.Date, checkIn.Notes, recordedBy);
            vouchers.Add(voucher);
            return (asset, voucher);
        }

        var checkOut = request.CheckOut!;
        var name = asset.Name;
        var made = asset.CheckOut(checkOut.TransactionId, checkOut.Reason, checkOut.Quantity, checkOut.Date, checkOut.Notes, recordedBy);
        vouchers.Add(made);
        // A consumable broken: a cost of the day, in Costing → Waste.
        if (made.Reason == AssetMovementReason.Breakage
            && asset.CategoryId is { } categoryId
            && await categories.GetByIdAsync(categoryId, cancellationToken) is { IsConsumable: true })
        {
            var entry = new BreakageEntry(
                Guid.NewGuid(), made.Date, asset.Id, made.Id, name, made.Quantity, made.UnitCost, made.EmployeeId, made.Notes, recordedBy);
            breakage.Add(entry);
            made.RouteToWaste(entry.Id);
        }

        return (asset, made);
    }

    private void CheckDate(DateOnly date)
    {
        if (date > clock.BusinessDateOf(DateTime.UtcNow))
            throw new DomainException("A voucher is dated today or before.");
    }

    public static string ReasonOf(AssetMovementReason reason) => reason switch
    {
        AssetMovementReason.Returned => "Handed back",
        AssetMovementReason.Maintenance => "To maintenance",
        AssetMovementReason.Breakage => "Broken",
        AssetMovementReason.Lost => "Lost",
        AssetMovementReason.Discarded => "Thrown away",
        _ => reason.ToString(),
    };

    private static string Cost(decimal? cost) => cost is { } c ? $"L.E {c.ToString("N2", Invariant)}" : "Not known";

    private static string Day(DateOnly day) => day.ToString("d MMM yyyy", Invariant);
}

// An asset added or changed, or a voucher for it. RequestId is made by the till: a retry finds
// what was asked; a voucher's TransactionId, the voucher made.
public sealed record ChangeCustodyCommand(
    Guid AssetId, Guid RequestId, CustodyRequest Change, string? Reason, string StaffId, string StaffName, bool CanApprove)
    : IRequest<CustodyResultDto>, IRetryOnConflict;

internal sealed class ChangeCustodyHandler(
    CustodyWriter writer,
    IAssetItemRepository assets,
    IAssetTransactionRepository vouchers,
    IAssetCategoryRepository categories,
    IEmployeeRepository employees,
    IApprovalRequestRepository requests,
    IUnitOfWork unitOfWork) : IRequestHandler<ChangeCustodyCommand, CustodyResultDto>
{
    public async Task<CustodyResultDto> Handle(ChangeCustodyCommand request, CancellationToken cancellationToken)
    {
        if (await requests.GetByIdAsync(request.RequestId, cancellationToken) is { } already)
            return already.IsPending
                ? new CustodyResultDto(ApprovalOutcome.Requested, null, null, ApprovalRequestDto.From(already))
                : await AppliedAsync(request, ApprovalRequestDto.From(already), cancellationToken);
        if (VoucherId(request.Change) is { } voucherId && await vouchers.GetByIdAsync(voucherId, cancellationToken) is not null)
            return await AppliedAsync(request, null, cancellationToken);
        await requests.EnsureNotPendingAsync(request.AssetId, cancellationToken);
        var (summary, action, changes) = await writer.DescribeAsync(request.AssetId, request.Change, cancellationToken);

        if (request.CanApprove)
        {
            await writer.ApplyAsync(request.AssetId, request.Change, request.StaffName, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return await AppliedAsync(request, null, cancellationToken);
        }

        await writer.CheckAsync(request.AssetId, request.Change, cancellationToken);
        var asked = ApprovalRequest.AskToChange(
            request.RequestId, ApprovalModule.Custody, action, request.AssetId, summary, changes, ApprovalPayload.Write(request.Change),
            request.Reason, request.StaffId, request.StaffName);
        requests.Add(asked);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new CustodyResultDto(ApprovalOutcome.Requested, null, null, ApprovalRequestDto.From(asked));
    }

    private static Guid? VoucherId(CustodyRequest change) => change.CheckIn?.TransactionId ?? change.CheckOut?.TransactionId;

    private async Task<CustodyResultDto> AppliedAsync(ChangeCustodyCommand request, ApprovalRequestDto? decided, CancellationToken cancellationToken)
    {
        var names = await CustodyNames.ReadAsync(categories, employees, cancellationToken);
        var asset = await assets.GetByIdAsync(request.AssetId, cancellationToken);
        var voucher = VoucherId(request.Change) is { } id ? await vouchers.GetByIdAsync(id, cancellationToken) : null;
        return new CustodyResultDto(
            ApprovalOutcome.Applied, asset is null ? null : names.Asset(asset), voucher is null || asset is null ? null : names.Voucher(voucher, asset.Name), decided);
    }
}

// The assets a posted invoice bought: each line of an expense marked as an asset, an asset in the
// store of its category, its quantity at the line's cost a piece (a quantity that is not a whole
// number is one, at the line's cost). One asset to a line, however often the invoice is read.
// A voided invoice takes its assets with it while none has been handed out.
internal sealed class PurchasedAssets(IExpenseItemRepository expenses, IAssetItemRepository assets, IAssetTransactionRepository vouchers)
{
    public async Task MakeAsync(OtantikPos.Inventory.Domain.Purchases.PurchaseInvoice invoice, string by, CancellationToken cancellationToken)
    {
        var lines = invoice.Lines.Where(l => l.ExpenseItemId is not null).ToList();
        if (lines.Count == 0)
            return;
        var marked = (await expenses.GetByIdsAsync(lines.Select(l => l.ExpenseItemId!.Value).Distinct().ToList(), cancellationToken))
            .Where(e => e.IsAsset)
            .ToDictionary(e => e.Id);
        foreach (var line in lines)
        {
            if (!marked.TryGetValue(line.ExpenseItemId!.Value, out var item) || await assets.GetBySourceLineAsync(line.Id, cancellationToken) is not null)
                continue;
            var whole = line.Quantity == Math.Floor(line.Quantity) && line.Quantity is >= 1 and <= AssetItem.MaxQuantity;
            var quantity = whole ? (int)line.Quantity : 1;
            decimal? unitCost = line.Cost is { } cost ? Math.Round(cost / quantity, 2, MidpointRounding.AwayFromZero) : null;
            assets.Add(new AssetItem(
                Guid.NewGuid(), item.Name, item.AssetCategoryId, unitCost, quantity, AssetOrigin.FinancialPurchase, by, invoice.Id, line.Id,
                whole ? null : $"{line.Quantity.ToString("0.###", CultureInfo.InvariantCulture)} {line.UnitLabel} on the invoice"));
        }
    }

    // Refused while one of them has been handed out or moved: their vouchers are its record.
    public async Task CheckVoidAsync(OtantikPos.Inventory.Domain.Purchases.PurchaseInvoice invoice, CancellationToken cancellationToken)
    {
        foreach (var asset in await assets.GetFromInvoiceAsync(invoice.Id, cancellationToken))
            if (await vouchers.AnyForAssetAsync(asset.Id, cancellationToken))
                throw new DomainException($"{asset.Name}, bought on this invoice, has been handed out in Custody & Assets: its vouchers keep it.");
    }

    public async Task RemoveAsync(OtantikPos.Inventory.Domain.Purchases.PurchaseInvoice invoice, CancellationToken cancellationToken)
    {
        await CheckVoidAsync(invoice, cancellationToken);
        foreach (var asset in await assets.GetFromInvoiceAsync(invoice.Id, cancellationToken))
            assets.Remove(asset);
    }
}
