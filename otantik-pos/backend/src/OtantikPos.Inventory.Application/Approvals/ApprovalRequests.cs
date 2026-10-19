using System.Text.Json;
using System.Text.Json.Serialization;
using MediatR;
using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Application.Categories;
using OtantikPos.Inventory.Application.Common;
using OtantikPos.Inventory.Application.Production;
using OtantikPos.Inventory.Application.Purchases;
using OtantikPos.Inventory.Application.RawMaterials;
using OtantikPos.Inventory.Application.Treasury;
using OtantikPos.Inventory.Domain.Approvals;
using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Inventory.Domain.Recipes;
using OtantikPos.Inventory.Domain.StockMovements;

namespace OtantikPos.Inventory.Application.Approvals;

// Maker-checker for the costing modules. Whoever may decide (ApprovalDecide) acts at once;
// anyone else asks, and nothing changes until a manager approves: a deletion (Deletions.cs) in
// recipes, purchases, waste, stock counts, raw materials, prep items and production orders, and
// a new or changed prep item or production order (PrepItems.cs, ProductionOrders.cs). One table
// holds them all, keyed by the record each is about; a record with a request waiting is locked.

// What a summary needs from outside the inventory module: a menu item's name for a recipe
// linked to it, and the restaurant's business date of a moment.
public interface IRecordDescriptions
{
    Task<string?> MenuItemNameAsync(RecipeTargetKind kind, int catalogItemId, int? variantId, CancellationToken cancellationToken);

    DateOnly BusinessDateOf(DateTime utc);

    // The calendar date in the restaurant: after midnight, a day ahead of the business date
    // until the business day starts.
    DateOnly LocalDateOf(DateTime utc);
}

public enum ApprovalOutcome
{
    // Asked: waiting for a manager.
    Requested = 1,

    // Done: the asker could approve it.
    Applied = 2,
}

public sealed record ApprovalRequestDto(
    Guid Id,
    ApprovalModule Module,
    ApprovalAction Action,
    Guid RecordId,
    string Summary,
    IReadOnlyList<ApprovalChange> Changes,
    string? Reason,
    string RequestedBy,
    DateTime RequestedAtUtc,
    ApprovalStatus Status,
    string? DecidedBy,
    DateTime? DecidedAtUtc,
    string? DecisionNote)
{
    public static ApprovalRequestDto From(ApprovalRequest r) => new(
        r.Id, r.Module, r.Action, r.RecordId, r.Summary, r.Changes, r.Reason, r.RequestedBy, r.RequestedAtUtc, r.Status, r.DecidedBy,
        r.DecidedAtUtc, r.DecisionNote);
}

public sealed record ApprovalResultDto(ApprovalOutcome Outcome, ApprovalRequestDto Request)
{
    // Requested while it waits; Applied once approved.
    public static ApprovalResultDto Of(ApprovalRequest request) =>
        new(request.IsPending ? ApprovalOutcome.Requested : ApprovalOutcome.Applied, ApprovalRequestDto.From(request));
}

public static class ApprovalLocks
{
    // A record with a request waiting is not changed meanwhile: the manager decides on what was
    // asked about.
    public static async Task EnsureNotPendingAsync(this IApprovalRequestRepository requests, Guid recordId, CancellationToken cancellationToken)
    {
        if (await requests.GetPendingForAsync(recordId, cancellationToken) is { } pending)
            throw new DomainException($"{pending.RequestedBy} has asked {What(pending)}. It can be changed once a manager has decided.");
    }

    // "for this to be deleted", "to change this".
    internal static string What(ApprovalRequest pending) => pending.Action switch
    {
        ApprovalAction.Delete => "for this to be deleted",
        ApprovalAction.Update => "to change this",
        _ when pending.Module == ApprovalModule.Treasury => "to pay this",
        _ => "for this",
    };
}

// What a new or changed record holds while it waits, as JSON: enums by name, so the stored
// request reads plainly.
internal static class ApprovalPayload
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static string Write<T>(T change) => JsonSerializer.Serialize(change, Options);

    public static T Read<T>(ApprovalRequest request) =>
        JsonSerializer.Deserialize<T>(request.Payload ?? throw new DomainException("This request holds no change to apply."), Options)
        ?? throw new DomainException("This request's change could not be read.");
}

// A manager's yes: what was asked is done (deleted, voided, archived, added, changed, made) and
// the request closed, together. Approving again, after a lost answer, changes nothing. When it
// can no longer be done (a price missing, an ingredient gone), it stays waiting, for the manager
// to put right or reject.
public sealed record ApproveRequestCommand(Guid RequestId, string StaffName) : IRequest<ApprovalRequestDto>, IRetryOnConflict;

internal sealed class ApproveRequestHandler(
    IApprovalRequestRepository requests,
    DeletionTargets deletions,
    PrepItemWriter prepItems,
    ProductionWriter production,
    TreasuryWriter treasury,
    InvoiceCorrector invoices,
    MaterialUnitWriter units,
    CategoryDeleter categoryDeleter,
    IRawMaterialRepository materials,
    IStockMovementRepository movements,
    IUnitOfWork unitOfWork) : IRequestHandler<ApproveRequestCommand, ApprovalRequestDto>
{
    public async Task<ApprovalRequestDto> Handle(ApproveRequestCommand request, CancellationToken cancellationToken)
    {
        var approval = await requests.GetRequiredAsync(request.RequestId, cancellationToken);
        if (approval.Status == ApprovalStatus.Approved)
            return ApprovalRequestDto.From(approval);

        approval.Approve(request.StaffName);
        switch (approval.Action, approval.Module)
        {
            // A category deleted, what is in it moved and converted as asked, and saved with it.
            case (ApprovalAction.Delete, ApprovalModule.PurchaseCategory):
                await categoryDeleter.ApplyApprovedAsync(
                    approval.RecordId, ApprovalPayload.Read<CategoryDeletion>(approval), approval.Id, request.StaffName, cancellationToken);
                break;

            case (ApprovalAction.Delete, _):
                foreach (var reversal in await deletions.DeleteAsync(approval.Module, approval.RecordId, request.StaffName, cancellationToken))
                    movements.Add(reversal);
                break;

            case (_, ApprovalModule.PrepItem):
                await prepItems.SaveAsync(
                    ApprovalPayload.Read<PrepItemChange>(approval), approval.Action == ApprovalAction.Update ? approval.RecordId : null,
                    approval.RecordId, cancellationToken);
                break;

            case (ApprovalAction.Create, ApprovalModule.ProductionOrder):
                await production.MakeAsync(
                    ApprovalPayload.Read<ProductionChange>(approval), approval.RequestedBy, request.StaffName, cancellationToken);
                break;

            case (ApprovalAction.Create, ApprovalModule.Treasury):
                await treasury.ApplyAsync(ApprovalPayload.Read<SafeVoucher>(approval), approval.RequestedBy, request.StaffName, cancellationToken);
                break;

            // An invoice corrected: the original voided, the correction posted in its place.
            case (ApprovalAction.Update, ApprovalModule.PurchaseInvoice):
                await invoices.ApplyAsync(
                    await invoices.OriginalAsync(approval.RecordId, cancellationToken), ApprovalPayload.Read<InvoiceCorrection>(approval),
                    approval.RequestedBy, request.StaffName, cancellationToken);
                break;

            // A material counted in another unit: everything recorded restated, and saved with it.
            case (ApprovalAction.Update, ApprovalModule.RawMaterial):
                await units.ApplyAsync(
                    await materials.GetRequiredAsync(approval.RecordId, cancellationToken), ApprovalPayload.Read<MaterialUnitChange>(approval), approval.Id,
                    cancellationToken);
                break;

            case (ApprovalAction.Update, ApprovalModule.ProductionOrder):
                await production.ReviseAsync(
                    await production.OrderAsync(approval.RecordId, cancellationToken), ApprovalPayload.Read<ProductionChange>(approval),
                    by: request.StaffName, approvedBy: request.StaffName, cancellationToken);
                break;

            default:
                throw new DomainException("This request cannot be approved here.");
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ApprovalRequestDto.From(approval);
    }
}

// A manager's no: the record stays as it is, unlocked; a new one is never made. The request is
// kept, with the note.
public sealed record RejectRequestCommand(Guid RequestId, string? Note, string StaffName) : IRequest<ApprovalRequestDto>;

internal sealed class RejectRequestHandler(IApprovalRequestRepository requests, IUnitOfWork unitOfWork)
    : IRequestHandler<RejectRequestCommand, ApprovalRequestDto>
{
    public async Task<ApprovalRequestDto> Handle(RejectRequestCommand request, CancellationToken cancellationToken)
    {
        var approval = await requests.GetRequiredAsync(request.RequestId, cancellationToken);
        approval.Reject(request.StaffName, request.Note);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ApprovalRequestDto.From(approval);
    }
}

// What waits for a decision, oldest first; with Decided, also the last decisions, newest first.
public sealed record GetApprovalRequestsQuery(int Decided = 0) : IRequest<ApprovalRequestsDto>;

public sealed record ApprovalRequestsDto(IReadOnlyList<ApprovalRequestDto> Pending, IReadOnlyList<ApprovalRequestDto> Decided);

internal sealed class GetApprovalRequestsHandler(IApprovalRequestRepository requests) : IRequestHandler<GetApprovalRequestsQuery, ApprovalRequestsDto>
{
    public async Task<ApprovalRequestsDto> Handle(GetApprovalRequestsQuery request, CancellationToken cancellationToken) => new(
        (await requests.GetPendingAsync(cancellationToken)).Select(ApprovalRequestDto.From).ToList(),
        request.Decided > 0
            ? (await requests.GetDecidedAsync(request.Decided, cancellationToken)).Select(ApprovalRequestDto.From).ToList()
            : []);
}

// What approving a batch waiting for it would move, worked out now: each ingredient taken at its
// average cost at this moment, and the prep item put in at their total. For a correction, also
// the batch it replaces, whose entries go back first: the manager sees the net movement. What the
// manager signs off on. Null for any other request.
public sealed record ApprovalPreviewDto(ProductionPreviewDto Batch, ProductionOrderDto? Replaces);

public sealed record GetApprovalPreviewQuery(Guid RequestId) : IRequest<ApprovalPreviewDto?>;

internal sealed class GetApprovalPreviewHandler(IApprovalRequestRepository requests, ProductionWriter production, IRawMaterialRepository materials)
    : IRequestHandler<GetApprovalPreviewQuery, ApprovalPreviewDto?>
{
    public async Task<ApprovalPreviewDto?> Handle(GetApprovalPreviewQuery request, CancellationToken cancellationToken)
    {
        var approval = await requests.GetRequiredAsync(request.RequestId, cancellationToken);
        if (approval.Module != ApprovalModule.ProductionOrder || approval.Action == ApprovalAction.Delete || !approval.IsPending)
            return null;
        var change = ApprovalPayload.Read<ProductionChange>(approval);
        var batch = await production.PreviewAsync(change.PrepItemId, change.PlannedQuantity, change.ProducedQuantity, cancellationToken);
        if (approval.Action != ApprovalAction.Update)
            return new ApprovalPreviewDto(batch, null);
        var replaced = await production.OrderAsync(approval.RecordId, cancellationToken);
        return new ApprovalPreviewDto(batch, ProductionOrderDto.From(replaced, (await materials.GetAllAsync(cancellationToken)).ToDictionary(m => m.Id)));
    }
}
