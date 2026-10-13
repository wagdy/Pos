using System.Globalization;
using MediatR;
using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Application.Approvals;
using OtantikPos.Inventory.Application.Common;
using OtantikPos.Inventory.Application.RawMaterials;
using OtantikPos.Inventory.Domain.Approvals;
using OtantikPos.Inventory.Domain.Production;
using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Inventory.Domain.StockMovements;

namespace OtantikPos.Inventory.Application.Production;

// Production orders: the kitchen's prep work, logged. A prep item, how much was planned (which
// sets the ingredients) and how much came out; posting takes the ingredients off stock at their
// average cost and puts the prep item in at their total over what came out. OrderId is made by
// the till. Quantities are in each material's own unit; costs per its display unit.
//
// A batch moves two stocks, so it is a manager's: anyone else's batch, correction or deletion is
// a request (ApprovalRequests.cs), and nothing moves while it waits. Approved, it is made then,
// at the averages of that moment. A batch is never edited: a correction reverses it (its entries
// undone) and makes it again, as a new batch that names the one it replaced.

// One ingredient of a batch. OnHand and Short only on a preview: what the records show now, and
// how much more the batch takes than that.
public sealed record ProductionLineDto(
    Guid RawMaterialId,
    string? Code,
    string Material,
    bool IsPrepItem,
    UnitOfMeasure Unit,
    string PurchaseUnit,
    decimal PurchaseUnitSize,
    decimal Quantity,
    decimal? OnHand,
    decimal? Short,
    decimal? CostPerPurchaseUnit,
    decimal? Cost);

public sealed record ProductionOrderDto(
    Guid Id,
    Guid PrepItemId,
    string PrepItem,
    UnitOfMeasure Unit,
    string DisplayUnit,
    decimal DisplayUnitSize,
    decimal PlannedQuantity,
    decimal ProducedQuantity,
    // Produced against planned: under 100 is what was lost in the making.
    decimal YieldPercent,
    decimal TotalCost,
    decimal UnitCostPerDisplayUnit,
    DateTime ProducedAtUtc,
    string ProducedBy,
    string? Notes,
    IReadOnlyList<ProductionLineDto> Lines,
    ProductionOrderStatus Status,
    DateTime? VoidedAtUtc,
    string? VoidedBy,
    // A correction: the batch it replaced; and on a replaced one, the batch that corrects it.
    Guid? RevisionOf,
    Guid? ReplacedBy,
    string? ApprovedBy)
{
    public static ProductionOrderDto From(ProductionOrder order, IReadOnlyDictionary<Guid, RawMaterial> materials)
    {
        var item = materials.GetValueOrDefault(order.PrepItemId);
        var size = item?.PurchaseUnitSize ?? 1;
        return new ProductionOrderDto(
            order.Id, order.PrepItemId, item?.Name ?? "A removed prep item", item?.Unit ?? UnitOfMeasure.Gram, item?.PurchaseUnit ?? "unit", size,
            order.PlannedQuantity, order.ProducedQuantity, Math.Round(order.ProducedQuantity * 100 / order.PlannedQuantity, 1),
            order.TotalCost, PrepItemCosting.Money(order.UnitCost * size), order.ProducedAtUtc, order.ProducedBy, order.Notes,
            order.Lines
                .Select(l => materials.GetValueOrDefault(l.RawMaterialId) is { } m
                    ? new ProductionLineDto(m.Id, m.Code, m.Name, m.IsPrepItem, m.Unit, m.PurchaseUnit, m.PurchaseUnitSize, l.Quantity, null, null,
                        PrepItemCosting.Money(l.UnitCost * m.PurchaseUnitSize), l.Cost)
                    : new ProductionLineDto(l.RawMaterialId, null, "A removed material", false, UnitOfMeasure.Piece, "unit", 1, l.Quantity, null, null, null, l.Cost))
                .OrderByDescending(l => l.Cost)
                .ToList(),
            order.Status, order.VoidedAtUtc, order.VoidedBy, order.RevisionOf, order.ReplacedBy, order.ApprovedBy);
    }
}

// What a batch would take and cost if made now: the review before it is posted.
public sealed record ProductionPreviewDto(
    Guid PrepItemId,
    string PrepItem,
    UnitOfMeasure Unit,
    string DisplayUnit,
    decimal DisplayUnitSize,
    decimal PlannedQuantity,
    decimal ProducedQuantity,
    IReadOnlyList<ProductionLineDto> Lines,
    decimal? TotalCost,
    decimal? UnitCostPerDisplayUnit,
    // No cost yet: the batch cannot be posted until they have one.
    IReadOnlyList<string> MissingPrices,
    // Less on record than the batch takes: it can still be posted, and goes below zero.
    IReadOnlyList<string> Shortages);

// A batch as entered, held by a request until a manager approves it: which prep item, how much,
// and the id it is made under (for a correction, the new batch's).
public sealed record ProductionChange(Guid OrderId, Guid PrepItemId, decimal PlannedQuantity, decimal ProducedQuantity, string? Notes);

// Makes, voids and corrects batches, for a manager's own action and for an approval alike. What
// it makes and reverses is added to the orders and the ledger, for the caller to save.
internal sealed class ProductionWriter(
    IProductionOrderRepository orders,
    IPrepRecipeRepository recipes,
    IRawMaterialRepository materials,
    IStockMovementRepository movements,
    IRecordDescriptions descriptions)
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public async Task<ProductionOrder> OrderAsync(Guid id, CancellationToken cancellationToken) =>
        await orders.GetByIdAsync(id, cancellationToken) ?? throw new NotFoundException(nameof(ProductionOrder), id);

    public async Task<ProductionPreviewDto> PreviewAsync(Guid prepItemId, decimal planned, decimal? produced, CancellationToken cancellationToken)
    {
        var all = (await materials.GetAllAsync(cancellationToken)).ToDictionary(m => m.Id);
        if (all.GetValueOrDefault(prepItemId) is not { IsPrepItem: true } item)
            throw new NotFoundException("Prep item", prepItemId);
        if (item.IsArchived)
            throw new DomainException($"{item.Name} has been deleted: it is no longer made.");
        var recipe = await recipes.GetByIdAsync(item.Id, cancellationToken);
        if (recipe is null || recipe.Ingredients.Count == 0)
            throw new DomainException($"{item.Name} has no ingredients yet: write its recipe first.");
        var made = produced ?? planned;
        if (made <= 0)
            throw new DomainException("Enter how much was made, more than nothing.");

        var lines = recipe.RequirementsFor(planned)
            .Select(r =>
            {
                var m = all.GetValueOrDefault(r.RawMaterialId) ?? throw new NotFoundException(nameof(RawMaterial), r.RawMaterialId);
                return new ProductionLineDto(
                    m.Id, m.Code, m.Name, m.IsPrepItem, m.Unit, m.PurchaseUnit, m.PurchaseUnitSize, r.Quantity, m.QuantityOnHand,
                    r.Quantity > m.QuantityOnHand ? r.Quantity - Math.Max(m.QuantityOnHand, 0) : null,
                    m.CostPerPurchaseUnit is { } c ? PrepItemCosting.Money(c) : null,
                    m.AverageCost is { } unit ? PrepItemCosting.Money(r.Quantity * unit) : null);
            })
            .OrderByDescending(l => l.Cost ?? 0)
            .ToList();
        var missing = lines.Where(l => l.Cost is null).Select(l => l.Material).ToList();
        decimal? total = missing.Count == 0 ? lines.Sum(l => l.Cost!.Value) : null;

        return new ProductionPreviewDto(
            item.Id, item.Name, item.Unit, item.PurchaseUnit, item.PurchaseUnitSize, planned, made, lines,
            total, total is { } t ? PrepItemCosting.Money(t / made * item.PurchaseUnitSize) : null,
            missing, lines.Where(l => l.Short is not null).Select(l => l.Material).ToList());
    }

    // What would stop it being made now, refused before a request goes to a manager. It is made
    // on approval, at the prices then, and checked again there.
    public async Task CheckAsync(ProductionChange change, CancellationToken cancellationToken)
    {
        if (change.PlannedQuantity <= 0 || change.ProducedQuantity <= 0)
            throw new DomainException("Enter how much was planned and how much was made, both more than nothing.");
        if (change.Notes?.Trim().Length > 200)
            throw new DomainException("Keep the notes under 200 characters.");
        var preview = await PreviewAsync(change.PrepItemId, change.PlannedQuantity, change.ProducedQuantity, cancellationToken);
        if (preview.MissingPrices.Count > 0)
            throw new DomainException(
                $"{string.Join(", ", preview.MissingPrices)} {(preview.MissingPrices.Count == 1 ? "has" : "have")} no cost yet, so the batch's cost cannot be worked out. "
                + "Enter it on Raw materials, or post the invoice it came on.");
    }

    // Makes the batch: the ingredients out at their averages now, the prep item in at their total.
    public async Task<ProductionOrder> MakeAsync(
        ProductionChange change, string producedBy, string? approvedBy, CancellationToken cancellationToken, Guid? revisionOf = null)
    {
        var item = await materials.GetRequiredAsync(change.PrepItemId, cancellationToken);
        if (!item.IsPrepItem)
            throw new DomainException($"{item.Name} is bought, not made: it comes in on a purchase invoice.");
        var recipe = await recipes.GetByIdAsync(item.Id, cancellationToken)
            ?? throw new DomainException($"{item.Name} has no ingredients yet: write its recipe first.");

        // Tracked, as they change: their stock and, for the prep item, its average.
        var ingredients = recipe.Ingredients.Count == 0
            ? new Dictionary<Guid, RawMaterial>()
            : (await materials.GetByIdsAsync(recipe.Ingredients.Select(i => i.RawMaterialId).ToList(), cancellationToken)).ToDictionary(m => m.Id);

        var (order, ledger) = ProductionOrder.Make(
            change.OrderId, item, recipe, ingredients, change.PlannedQuantity, change.ProducedQuantity, producedBy, change.Notes, approvedBy, revisionOf);
        orders.Add(order);
        foreach (var movement in ledger)
            movements.Add(movement);
        return order;
    }

    // Reverses each of its entries, now: the ingredients back at what they were taken at, the
    // prep item out with its cost taken out of the average. Then marks it voided.
    public async Task VoidAsync(ProductionOrder order, string by, Guid? replacedBy, CancellationToken cancellationToken)
    {
        if (order.IsVoided)
            throw new DomainException("This batch has already been voided.");
        var originals = (await movements.GetBySourceAsync(order.Id, StockMovementReason.ProductionInput, cancellationToken))
            .Concat(await movements.GetBySourceAsync(order.Id, StockMovementReason.ProductionOutput, cancellationToken))
            .Where(m => !m.IsReversal)
            .ToList();
        if (originals.Count > 0)
        {
            var byId = await materials.GetRequiredByIdsAsync(originals.Select(m => m.RawMaterialId).Distinct().ToList(), cancellationToken);
            foreach (var original in originals)
                movements.Add(byId[original.RawMaterialId].Reverse(original, by));
        }

        order.Void(by, replacedBy);
    }

    // A correction: the batch reversed, then made again with the quantities given, at the
    // averages now, under the change's id. Kept by whoever made the first.
    public async Task<ProductionOrder> ReviseAsync(
        ProductionOrder original, ProductionChange change, string by, string? approvedBy, CancellationToken cancellationToken)
    {
        if (change.PrepItemId != original.PrepItemId)
            throw new DomainException("A correction makes the same prep item; void the batch and make the other instead.");
        await VoidAsync(original, by, change.OrderId, cancellationToken);
        return await MakeAsync(change, original.ProducedBy, approvedBy, cancellationToken, original.Id);
    }

    // "Batch of Pizza dough made 8 Oct 2026 by Omar · 9.6 kg · L.E 123.40"
    public static string Describe(ProductionOrder order, RawMaterial? item, IRecordDescriptions descriptions) =>
        $"Batch of {item?.Name ?? "a removed prep item"} made {descriptions.BusinessDateOf(order.ProducedAtUtc).ToString("d MMM yyyy", Invariant)}" +
        $"{(order.ProducedBy is { Length: > 0 } by ? $" by {by}" : "")} · " +
        $"{(item is null ? order.ProducedQuantity.ToString("0.###", Invariant) : PrepItemCosting.Amount(order.ProducedQuantity, item))} · " +
        $"L.E {order.TotalCost.ToString("N2", Invariant)}";

    // What a request would make, for the manager: the quantities and what each ingredient gives,
    // as new, or for a correction against the batch as it was made.
    public async Task<(string Summary, IReadOnlyList<ApprovalChange> Changes)> DescribeAsync(
        ProductionChange change, ProductionOrder? original, CancellationToken cancellationToken)
    {
        var all = (await materials.GetAllAsync(cancellationToken)).ToDictionary(m => m.Id);
        var item = all[change.PrepItemId];
        var recipe = await recipes.GetByIdAsync(item.Id, cancellationToken)
            ?? throw new DomainException($"{item.Name} has no ingredients yet: write its recipe first.");
        var takes = recipe.RequirementsFor(change.PlannedQuantity).ToDictionary(r => r.RawMaterialId, r => r.Quantity);
        var took = original?.Lines.ToDictionary(l => l.RawMaterialId, l => l.Quantity) ?? [];
        string Of(Guid id) => all.GetValueOrDefault(id)?.Name ?? "A removed material";
        string? Taken(Dictionary<Guid, decimal> from, Guid id) =>
            from.TryGetValue(id, out var quantity) && all.GetValueOrDefault(id) is { } m ? PrepItemCosting.Readable(quantity, m.Unit) : null;

        var changes = new List<ApprovalChange>();
        void Add(string label, string? before, string? after)
        {
            if (before != after)
                changes.Add(new ApprovalChange(label, before, after));
        }

        if (original is null)
            Add("Prep item", null, item.Name);
        Add("Quantity to produce", original is null ? null : PrepItemCosting.Amount(original.PlannedQuantity, item), PrepItemCosting.Amount(change.PlannedQuantity, item));
        Add("Came out", original is null ? null : PrepItemCosting.Amount(original.ProducedQuantity, item), PrepItemCosting.Amount(change.ProducedQuantity, item));
        Add("Notes", original?.Notes, string.IsNullOrWhiteSpace(change.Notes) ? null : change.Notes.Trim());
        foreach (var id in took.Keys.Union(takes.Keys).OrderBy(Of))
            Add($"{Of(id)} taken", Taken(took, id), Taken(takes, id));

        var summary = original is null
            ? $"New batch of {item.Name} · {PrepItemCosting.Amount(change.PlannedQuantity, item)} planned, " +
              $"{PrepItemCosting.Amount(change.ProducedQuantity, item)} came out"
            : Describe(original, item, descriptions);
        return (summary, changes);
    }
}

public sealed record PreviewProductionQuery(Guid PrepItemId, decimal PlannedQuantity, decimal? ProducedQuantity = null)
    : IRequest<ProductionPreviewDto>;

internal sealed class PreviewProductionQueryHandler(ProductionWriter production)
    : IRequestHandler<PreviewProductionQuery, ProductionPreviewDto>
{
    public Task<ProductionPreviewDto> Handle(PreviewProductionQuery request, CancellationToken cancellationToken) =>
        production.PreviewAsync(request.PrepItemId, request.PlannedQuantity, request.ProducedQuantity, cancellationToken);
}

// Makes the batch, for a manager. Posting again, after an answer was lost, returns the order and
// changes nothing.
public sealed record MakeProductionOrderCommand(
    Guid OrderId, Guid PrepItemId, decimal PlannedQuantity, decimal ProducedQuantity, string? Notes = null, string StaffName = "")
    : IRequest<ProductionOrderDto>, IRetryOnConflict;

internal sealed class MakeProductionOrderCommandHandler(
    IProductionOrderRepository orders,
    IRawMaterialRepository materials,
    ProductionWriter production,
    IUnitOfWork unitOfWork) : IRequestHandler<MakeProductionOrderCommand, ProductionOrderDto>
{
    public async Task<ProductionOrderDto> Handle(MakeProductionOrderCommand request, CancellationToken cancellationToken)
    {
        var order = await orders.GetByIdAsync(request.OrderId, cancellationToken);
        if (order is null)
        {
            order = await production.MakeAsync(
                new ProductionChange(request.OrderId, request.PrepItemId, request.PlannedQuantity, request.ProducedQuantity, request.Notes),
                request.StaffName, null, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return ProductionOrderDto.From(order, (await materials.GetAllAsync(cancellationToken)).ToDictionary(m => m.Id));
    }
}

// A batch by someone who may not approve: asked of a manager, nothing moved until it is
// approved. Asking again for the same batch returns the request already made.
public sealed record RequestProductionOrderCommand(
    Guid RequestId,
    Guid OrderId,
    Guid PrepItemId,
    decimal PlannedQuantity,
    decimal ProducedQuantity,
    string? Notes,
    string? Reason,
    string StaffId,
    string StaffName) : IRequest<ApprovalResultDto>, IRetryOnConflict;

internal sealed class RequestProductionOrderHandler(
    IApprovalRequestRepository requests,
    IProductionOrderRepository orders,
    ProductionWriter production,
    IUnitOfWork unitOfWork) : IRequestHandler<RequestProductionOrderCommand, ApprovalResultDto>
{
    public async Task<ApprovalResultDto> Handle(RequestProductionOrderCommand request, CancellationToken cancellationToken)
    {
        if (await requests.GetByIdAsync(request.RequestId, cancellationToken) is { } already)
            return ApprovalResultDto.Of(already);
        if (await requests.GetPendingForAsync(request.OrderId, cancellationToken) is { } waiting)
            return ApprovalResultDto.Of(waiting);
        if (await orders.GetByIdAsync(request.OrderId, cancellationToken) is not null)
            throw new DomainException("This batch has already been made.");

        var change = new ProductionChange(
            request.OrderId, request.PrepItemId, request.PlannedQuantity, request.ProducedQuantity,
            string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim());
        await production.CheckAsync(change, cancellationToken);
        var (summary, changes) = await production.DescribeAsync(change, null, cancellationToken);
        var asked = ApprovalRequest.AskToChange(
            request.RequestId, ApprovalModule.ProductionOrder, ApprovalAction.Create, request.OrderId, summary, changes,
            ApprovalPayload.Write(change), request.Reason, request.StaffId, request.StaffName);
        requests.Add(asked);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ApprovalResultDto.Of(asked);
    }
}

// Corrects a batch made with the wrong quantities: reversed and made again with these, at the
// averages now, as a new batch (NewOrderId, made by the till). Done at once by someone who may
// approve, and recorded as approved by them, since it moves stock back; asked of a manager by
// anyone else.
public sealed record ReviseProductionOrderCommand(
    Guid RequestId,
    Guid OrderId,
    Guid NewOrderId,
    decimal PlannedQuantity,
    decimal ProducedQuantity,
    string? Notes,
    string? Reason,
    string StaffId,
    string StaffName,
    bool CanApprove) : IRequest<ApprovalResultDto>, IRetryOnConflict;

internal sealed class ReviseProductionOrderHandler(
    IApprovalRequestRepository requests,
    ProductionWriter production,
    IUnitOfWork unitOfWork) : IRequestHandler<ReviseProductionOrderCommand, ApprovalResultDto>
{
    public async Task<ApprovalResultDto> Handle(ReviseProductionOrderCommand request, CancellationToken cancellationToken)
    {
        if (await requests.GetByIdAsync(request.RequestId, cancellationToken) is { } already)
            return ApprovalResultDto.Of(already);

        var original = await production.OrderAsync(request.OrderId, cancellationToken);
        if (original.IsVoided)
            throw new DomainException(original.ReplacedBy is null
                ? "This batch has been voided."
                : "This batch has already been corrected: correct the batch that replaced it.");
        if (await requests.GetPendingForAsync(original.Id, cancellationToken) is { } pending)
            throw new DomainException($"{pending.RequestedBy} has already asked {ApprovalLocks.What(pending)}. A manager will decide.");

        var notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        if (request.PlannedQuantity == original.PlannedQuantity && request.ProducedQuantity == original.ProducedQuantity && notes == original.Notes)
            throw new DomainException("Nothing has changed: enter the quantities as they should have been.");
        var change = new ProductionChange(request.NewOrderId, original.PrepItemId, request.PlannedQuantity, request.ProducedQuantity, notes);
        await production.CheckAsync(change, cancellationToken);
        var (summary, changes) = await production.DescribeAsync(change, original, cancellationToken);
        var payload = ApprovalPayload.Write(change);

        if (!request.CanApprove)
        {
            var asked = ApprovalRequest.AskToChange(
                request.RequestId, ApprovalModule.ProductionOrder, ApprovalAction.Update, original.Id, summary, changes, payload,
                request.Reason, request.StaffId, request.StaffName);
            requests.Add(asked);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return ApprovalResultDto.Of(asked);
        }

        var decided = ApprovalRequest.DecidedChange(
            request.RequestId, ApprovalModule.ProductionOrder, ApprovalAction.Update, original.Id, summary, changes, payload,
            request.Reason, request.StaffId, request.StaffName);
        requests.Add(decided);
        await production.ReviseAsync(original, change, request.StaffName, null, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ApprovalResultDto.Of(decided);
    }
}

// The production ledger, newest first, voided batches too: one prep item's when it is given.
public sealed record GetProductionOrdersQuery(Guid? PrepItemId = null) : IRequest<IReadOnlyList<ProductionOrderDto>>;

internal sealed class GetProductionOrdersQueryHandler(IProductionOrderRepository orders, IRawMaterialRepository materials)
    : IRequestHandler<GetProductionOrdersQuery, IReadOnlyList<ProductionOrderDto>>
{
    public async Task<IReadOnlyList<ProductionOrderDto>> Handle(GetProductionOrdersQuery request, CancellationToken cancellationToken)
    {
        var all = (await materials.GetAllAsync(cancellationToken)).ToDictionary(m => m.Id);
        return (await orders.GetRecentAsync(request.PrepItemId, 200, cancellationToken))
            .Select(o => ProductionOrderDto.From(o, all))
            .ToList();
    }
}

public sealed record GetProductionOrderQuery(Guid OrderId) : IRequest<ProductionOrderDto?>;

internal sealed class GetProductionOrderQueryHandler(IProductionOrderRepository orders, IRawMaterialRepository materials)
    : IRequestHandler<GetProductionOrderQuery, ProductionOrderDto?>
{
    public async Task<ProductionOrderDto?> Handle(GetProductionOrderQuery request, CancellationToken cancellationToken) =>
        await orders.GetByIdAsync(request.OrderId, cancellationToken) is { } order
            ? ProductionOrderDto.From(order, (await materials.GetAllAsync(cancellationToken)).ToDictionary(m => m.Id))
            : null;
}
