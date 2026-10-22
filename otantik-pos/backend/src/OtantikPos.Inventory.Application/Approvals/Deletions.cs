using System.Globalization;
using MediatR;
using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Application.Common;
using OtantikPos.Inventory.Application.Production;
using OtantikPos.Inventory.Application.RawMaterials;
using OtantikPos.Hr.Domain;
using OtantikPos.Inventory.Application.Treasury;
using OtantikPos.Inventory.Domain.Approvals;
using OtantikPos.Inventory.Domain.Production;
using OtantikPos.Inventory.Domain.Purchases;
using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Inventory.Domain.Recipes;
using OtantikPos.Inventory.Domain.StockCounts;
using OtantikPos.Inventory.Domain.StockMovements;
using OtantikPos.Inventory.Domain.Suppliers;
using OtantikPos.Inventory.Domain.Treasury;

namespace OtantikPos.Inventory.Application.Approvals;

// Deletion, as maker-checker: whoever may approve deletes at once (and it is recorded as approved
// by them); anyone else asks, and the record is left as it is, counted and locked, until a
// manager decides. The API says which by CanApprove, from the signed-in role's permissions.

// Each module's record: described as it is now when deletion is asked for (refused there if it
// cannot be deleted), and deleted on approval. Every check is made again at approval, since the
// record may have changed in between.
internal sealed class DeletionTargets(
    IRecipeRepository recipes,
    IPurchaseInvoiceRepository invoices,
    ISupplierRepository suppliers,
    IStockMovementRepository movements,
    IStockCountRepository counts,
    IRawMaterialRepository materials,
    IPrepRecipeRepository prepRecipes,
    IProductionOrderRepository productionOrders,
    ProductionWriter production,
    ISafeTransactionRepository safe,
    TreasuryWriter treasury,
    IRecordDescriptions descriptions,
    IPayrollSlipRepository slips,
    IAdvanceAllocationRepository allocations)
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public async Task<string> DescribeAsync(ApprovalModule module, Guid recordId, CancellationToken cancellationToken) => module switch
    {
        ApprovalModule.Recipe => await DescribeRecipeAsync(await RecipeAsync(recordId, cancellationToken), cancellationToken),
        ApprovalModule.PurchaseInvoice => await DescribeInvoiceAsync(await InvoiceAsync(recordId, cancellationToken), cancellationToken),
        ApprovalModule.Waste => await DescribeWasteAsync(await WasteAsync(recordId, cancellationToken), cancellationToken),
        ApprovalModule.StockCount => DescribeCount(await CountAsync(recordId, cancellationToken)),
        ApprovalModule.RawMaterial => DescribeMaterial(await MaterialAsync(recordId, cancellationToken, check: true)),
        ApprovalModule.PrepItem => DescribeMaterial(await PrepItemAsync(recordId, cancellationToken)),
        ApprovalModule.ProductionOrder => await DescribeBatchAsync(await BatchAsync(recordId, cancellationToken), cancellationToken),
        ApprovalModule.Treasury => await treasury.DescribeAsync(await SafeEntryAsync(recordId, cancellationToken), cancellationToken),
        _ => throw new DomainException("That cannot be deleted here."),
    };

    // Returns the ledger entries a void adds, for the caller to save with the decision.
    public async Task<IReadOnlyList<StockMovement>> DeleteAsync(ApprovalModule module, Guid recordId, string by, CancellationToken cancellationToken)
    {
        switch (module)
        {
            case ApprovalModule.Recipe:
                (await RecipeAsync(recordId, cancellationToken)).Delete(by);
                return [];

            case ApprovalModule.PurchaseInvoice:
                return await VoidInvoiceAsync(await InvoiceAsync(recordId, cancellationToken), by, null, cancellationToken);

            case ApprovalModule.Waste:
                await WasteAsync(recordId, cancellationToken);
                return await ReverseAsync(recordId, StockMovementReason.Spoilage, by, cancellationToken);

            case ApprovalModule.StockCount:
            {
                var count = await CountAsync(recordId, cancellationToken);
                var reversals = await ReverseAsync(count.Id, StockMovementReason.CountAdjustment, by, cancellationToken);
                count.Void(by);
                return reversals;
            }

            case ApprovalModule.RawMaterial:
                (await MaterialAsync(recordId, cancellationToken, check: true)).Archive(by);
                return [];

            case ApprovalModule.PrepItem:
                (await PrepItemAsync(recordId, cancellationToken)).Archive(by);
                return [];

            // Its cash put back, dated today, by the writer, which adds it itself.
            case ApprovalModule.Treasury:
                await treasury.VoidAsync(await SafeEntryAsync(recordId, cancellationToken), by, cancellationToken);
                return [];

            // Its entries reversed by the writer, which adds them itself.
            case ApprovalModule.ProductionOrder:
                await production.VoidAsync(await BatchAsync(recordId, cancellationToken), by, null, cancellationToken);
                return [];

            default:
                throw new DomainException("That cannot be deleted here.");
        }
    }

    // ---- Recipes: soft-deleted ----

    private async Task<Recipe> RecipeAsync(Guid id, CancellationToken cancellationToken)
    {
        var recipe = await recipes.GetByIdAsync(id, cancellationToken) ?? throw new NotFoundException(nameof(Recipe), id);
        if (recipe.IsDeleted)
            throw new DomainException("This recipe has already been deleted.");
        return recipe;
    }

    private async Task<string> DescribeRecipeAsync(Recipe recipe, CancellationToken cancellationToken)
    {
        var linked = recipe.Target is { } target
            ? await descriptions.MenuItemNameAsync(target.Kind, target.CatalogItemId, target.VariantId, cancellationToken)
            : null;
        var name = recipe.Name ?? linked ?? "A recipe";
        return linked is not null && linked != name ? $"Recipe {name} (for {linked})" : $"Recipe {name}";
    }

    // ---- Purchase invoices: voided ----

    private async Task<PurchaseInvoice> InvoiceAsync(Guid id, CancellationToken cancellationToken)
    {
        var invoice = await invoices.GetByIdAsync(id, cancellationToken) ?? throw new NotFoundException(nameof(PurchaseInvoice), id);
        return invoice.Status switch
        {
            PurchaseInvoiceStatus.Posted => invoice,
            PurchaseInvoiceStatus.Draft => throw new DomainException("This invoice is a draft: discard it instead, no approval needed."),
            _ => throw new DomainException("This invoice has already been voided."),
        };
    }

    private async Task<string> DescribeInvoiceAsync(PurchaseInvoice invoice, CancellationToken cancellationToken)
    {
        var supplier = invoice.SupplierId is { } id ? (await suppliers.GetByIdAsync(id, cancellationToken))?.Name : null;
        var date = invoice.InvoiceDate ?? descriptions.BusinessDateOf(invoice.PostedAtUtc ?? invoice.CreatedAtUtc);
        return string.Join(" · ", new[]
        {
            invoice.SupplierInvoiceNumber is { } number ? $"Invoice {number}" : "Invoice",
            supplier,
            date.ToString("d MMM yyyy", Invariant),
            $"{invoice.Lines.Count} {(invoice.Lines.Count == 1 ? "line" : "lines")}",
            $"L.E {invoice.Total.ToString("N2", Invariant)}",
        }.Where(p => p is not null));
    }

    // ---- Waste entries: voided ----

    private async Task<IReadOnlyList<StockMovement>> WasteAsync(Guid spoilageId, CancellationToken cancellationToken)
    {
        var entries = await movements.GetBySourceAsync(spoilageId, StockMovementReason.Spoilage, cancellationToken);
        if (entries.Count == 0)
            throw new NotFoundException("Waste entry", spoilageId);
        if (entries.Any(m => m.IsReversal))
            throw new DomainException("This waste entry has already been voided.");
        return entries;
    }

    private async Task<string> DescribeWasteAsync(IReadOnlyList<StockMovement> entries, CancellationToken cancellationToken)
    {
        var byId = (await materials.GetByIdsAsync(entries.Select(e => e.RawMaterialId).Distinct(), cancellationToken)).ToDictionary(m => m.Id);
        var lines = entries
            .Select(e => byId.GetValueOrDefault(e.RawMaterialId) is { } m
                ? $"{m.Name} {(-e.Quantity / m.PurchaseUnitSize).ToString("0.###", Invariant)} {m.PurchaseUnit}{(e.Note is null ? "" : $" ({e.Note})")}"
                : "a removed material")
            .ToList();
        var value = entries.Sum(e => -e.Quantity * (e.UnitCost ?? 0));
        var first = entries[0];
        return $"Waste of {descriptions.BusinessDateOf(first.OccurredAtUtc).ToString("d MMM yyyy", Invariant)}" +
               (first.RecordedBy is null ? "" : $" by {first.RecordedBy}") +
               $": {string.Join(", ", lines.Take(3))}{(lines.Count > 3 ? $" and {lines.Count - 3} more" : "")} · L.E {value.ToString("N2", Invariant)}";
    }

    // ---- Stock counts: voided ----

    private async Task<StockCount> CountAsync(Guid id, CancellationToken cancellationToken)
    {
        var count = await counts.GetByIdAsync(id, cancellationToken) ?? throw new NotFoundException(nameof(StockCount), id);
        return count.Status switch
        {
            StockCountStatus.Posted => count,
            StockCountStatus.Draft => throw new DomainException("This count is a draft: discard it instead, no approval needed."),
            _ => throw new DomainException("This count has already been voided."),
        };
    }

    private string DescribeCount(StockCount count) =>
        $"Stock count posted {descriptions.BusinessDateOf(count.PostedAtUtc!.Value).ToString("d MMM yyyy", Invariant)}" +
        $"{(count.PostedBy is { Length: > 0 } by ? $" by {by}" : "")} · {count.Lines.Count} {(count.Lines.Count == 1 ? "material" : "materials")}";

    // ---- Raw materials: archived ----

    private async Task<RawMaterial> MaterialAsync(Guid id, CancellationToken cancellationToken, bool check)
    {
        var material = await materials.GetByIdAsync(id, cancellationToken) ?? throw new NotFoundException(nameof(RawMaterial), id);
        if (material.IsArchived)
            throw new DomainException($"{material.Name} has already been deleted.");
        if (!check)
            return material;

        var inRecipes = await recipes.CountUsingAsync(id, cancellationToken);
        var inPrep = (await prepRecipes.GetAllAsync(cancellationToken)).Count(r => r.Ingredients.Any(i => i.RawMaterialId == id));
        if (inRecipes + inPrep > 0)
            throw new DomainException(
                $"{material.Name} is in {inRecipes + inPrep} {(inRecipes + inPrep == 1 ? "recipe" : "recipes")}. Take it out of them first.");
        if (material.QuantityOnHand != 0)
            throw new DomainException(
                $"{material.Name} still has stock on hand. Count it to zero or record its waste first, so its value is not lost from the books.");
        return material;
    }

    // ---- Safe entries: voided ----

    private async Task<SafeTransaction> SafeEntryAsync(Guid id, CancellationToken cancellationToken)
    {
        var entry = await safe.GetByIdAsync(id, cancellationToken) ?? throw new NotFoundException("Safe entry", id);
        if (entry.IsReversal)
            throw new DomainException("This undoes another entry: it is not voided itself.");
        if (entry.IsVoided)
            throw new DomainException("This has already been voided.");
        // Deducted from a finalized slip: the pay was worked out with it.
        if (entry is { Kind: SafeTransactionKind.EmployeeAdvance, EmployeeId: { } employee, PayrollMonth: { } month }
            && await slips.GetAsync(employee, month, cancellationToken) is { IsFinalized: true })
            throw new DomainException($"This advance is deducted from the finalized pay of {month.ToString("MMMM yyyy", Invariant)}: it cannot be voided now.");
        // Given out to employees, in part or whole: its shares are taken back first.
        if (entry.IsPendingAdvance && (await allocations.GetForAdvancesAsync([entry.Id], cancellationToken)).Any(a => !a.IsVoided))
            throw new DomainException("Part of these advances is given out to employees: take those shares back on HR → Advances first.");
        return entry;
    }

    // ---- Prep items: archived, as a raw material is ----

    private async Task<RawMaterial> PrepItemAsync(Guid id, CancellationToken cancellationToken)
    {
        var item = await MaterialAsync(id, cancellationToken, check: true);
        return item.IsPrepItem ? item : throw new DomainException($"{item.Name} is a bought material, not a prep item.");
    }

    // ---- Production orders: voided, their entries reversed ----

    private async Task<ProductionOrder> BatchAsync(Guid id, CancellationToken cancellationToken)
    {
        var order = await productionOrders.GetByIdAsync(id, cancellationToken) ?? throw new NotFoundException(nameof(ProductionOrder), id);
        return order.IsVoided ? throw new DomainException("This batch has already been voided.") : order;
    }

    private async Task<string> DescribeBatchAsync(ProductionOrder order, CancellationToken cancellationToken) =>
        ProductionWriter.Describe(order, await materials.GetByIdAsync(order.PrepItemId, cancellationToken), descriptions);

    private static string DescribeMaterial(RawMaterial material) =>
        $"{(material.IsPrepItem ? "Prep item" : "Raw material")} {(material.Code is { } code ? $"{code} · " : "")}{material.Name}";

    // The reversing entries for what a source put in or took out, each material moved back.
    // A posted invoice voided: its goods back out of stock, its cash back where it came from, and
    // what it put on its supplier's account off it. replacedBy, the invoice correcting it.
    public async Task<IReadOnlyList<StockMovement>> VoidInvoiceAsync(PurchaseInvoice invoice, string by, Guid? replacedBy, CancellationToken cancellationToken)
    {
        var reversals = await ReverseAsync(invoice.Id, StockMovementReason.Purchase, by, cancellationToken);
        await treasury.UndoInvoiceAsync(invoice, by, cancellationToken);
        invoice.Void(by, replacedBy);
        return reversals;
    }

    private async Task<IReadOnlyList<StockMovement>> ReverseAsync(Guid sourceId, StockMovementReason reason, string by, CancellationToken cancellationToken)
    {
        var originals = (await movements.GetBySourceAsync(sourceId, reason, cancellationToken)).Where(m => !m.IsReversal).ToList();
        if (originals.Count == 0)
            return [];
        var byId = await materials.GetRequiredByIdsAsync(originals.Select(m => m.RawMaterialId).Distinct().ToList(), cancellationToken);
        return originals.Select(m => byId[m.RawMaterialId].Reverse(m, by)).ToList();
    }
}

// Deletes it, for someone who may approve; asks a manager to, for anyone else. RequestId is made
// by the till: a retry after a lost answer finds the request already made.
public sealed record RequestDeletionCommand(
    Guid RequestId, ApprovalModule Module, Guid RecordId, string? Reason, string StaffId, string StaffName, bool CanApprove)
    : IRequest<ApprovalResultDto>, IRetryOnConflict;

internal sealed class RequestDeletionHandler(
    IApprovalRequestRepository requests,
    DeletionTargets targets,
    IStockMovementRepository movements,
    IUnitOfWork unitOfWork) : IRequestHandler<RequestDeletionCommand, ApprovalResultDto>
{
    public async Task<ApprovalResultDto> Handle(RequestDeletionCommand request, CancellationToken cancellationToken)
    {
        if (await requests.GetByIdAsync(request.RequestId, cancellationToken) is { } already)
            return new ApprovalResultDto(already.IsPending ? ApprovalOutcome.Requested : ApprovalOutcome.Applied, ApprovalRequestDto.From(already));

        var pending = await requests.GetPendingForAsync(request.RecordId, cancellationToken);
        if (pending is not null && !request.CanApprove)
            throw new DomainException($"{pending.RequestedBy} has already asked {ApprovalLocks.What(pending)}. A manager will decide.");
        if (pending is not null && pending.Action != ApprovalAction.Delete)
            throw new DomainException($"{pending.RequestedBy} has asked {ApprovalLocks.What(pending)}: decide that first, on Pending approvals.");

        var summary = await targets.DescribeAsync(request.Module, request.RecordId, cancellationToken);
        ApprovalRequest decided;
        if (!request.CanApprove)
        {
            decided = ApprovalRequest.Ask(request.RequestId, request.Module, request.RecordId, summary, request.Reason, request.StaffId, request.StaffName);
            requests.Add(decided);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return new ApprovalResultDto(ApprovalOutcome.Requested, ApprovalRequestDto.From(decided));
        }

        // A manager deleting what someone has already asked about decides that request.
        if (pending is not null)
        {
            pending.Approve(request.StaffName);
            decided = pending;
        }
        else
        {
            decided = ApprovalRequest.Decided(request.RequestId, request.Module, request.RecordId, summary, request.Reason, request.StaffId, request.StaffName);
            requests.Add(decided);
        }

        foreach (var reversal in await targets.DeleteAsync(request.Module, request.RecordId, request.StaffName, cancellationToken))
            movements.Add(reversal);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new ApprovalResultDto(ApprovalOutcome.Applied, ApprovalRequestDto.From(decided));
    }
}
