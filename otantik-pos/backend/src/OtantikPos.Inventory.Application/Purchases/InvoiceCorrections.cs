using System.Globalization;
using MediatR;
using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Application.Approvals;
using OtantikPos.Inventory.Application.Common;
using OtantikPos.Inventory.Application.RawMaterials;
using OtantikPos.Inventory.Domain.Approvals;
using OtantikPos.Inventory.Domain.Expenses;
using OtantikPos.Inventory.Domain.Purchases;
using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Inventory.Domain.StockMovements;
using OtantikPos.Inventory.Domain.Suppliers;

namespace OtantikPos.Inventory.Application.Purchases;

// A posted invoice corrected (تعديل فاتورة): entered again as it should have been, as a new
// invoice (NewInvoiceId, made by the till) that names the one it corrects. Done, the original is
// voided (its goods out of stock, its cash back where it came from, its line off its supplier's
// account) and the correction posted in its place (its goods in at the averages now, paid as it
// says, its paper kept), together: nothing posted is rewritten, and both stay, linked. Everything
// on it may change; an invoice from before the treasury stays so, touching neither the safe nor
// a supplier's account.
//
// A manager's is done at once, recorded as decided by them; anyone else's is a request, and the
// original stays as it was, locked, until a manager approves it, or rejects it with a reason.
public sealed record InvoiceCorrection(
    Guid NewInvoiceId,
    Guid? SupplierId,
    string? SupplierInvoiceNumber,
    DateOnly? InvoiceDate,
    IReadOnlyList<PurchaseInvoiceLineEntry> Lines,
    PurchasePaymentMethod? PaymentMethod,
    DateOnly? RevenueDate);

public sealed record CorrectPurchaseInvoiceCommand(
    Guid RequestId, Guid InvoiceId, InvoiceCorrection Correction, string? Reason, string StaffId, string StaffName, bool CanApprove)
    : IRequest<ApprovalResultDto>, IRetryOnConflict;

internal sealed class CorrectPurchaseInvoiceHandler(IApprovalRequestRepository requests, InvoiceCorrector corrector, IUnitOfWork unitOfWork)
    : IRequestHandler<CorrectPurchaseInvoiceCommand, ApprovalResultDto>
{
    public async Task<ApprovalResultDto> Handle(CorrectPurchaseInvoiceCommand request, CancellationToken cancellationToken)
    {
        if (await requests.GetByIdAsync(request.RequestId, cancellationToken) is { } already)
            return ApprovalResultDto.Of(already);

        var original = await corrector.OriginalAsync(request.InvoiceId, cancellationToken);
        await requests.EnsureNotPendingAsync(original.Id, cancellationToken);
        var corrected = await corrector.BuildAsync(original, request.Correction, request.StaffName, track: false, cancellationToken);
        var (summary, changes) = await corrector.DescribeAsync(original, corrected, cancellationToken);
        var payload = ApprovalPayload.Write(request.Correction);

        if (!request.CanApprove)
        {
            var asked = ApprovalRequest.AskToChange(
                request.RequestId, ApprovalModule.PurchaseInvoice, ApprovalAction.Update, original.Id, summary, changes, payload, request.Reason,
                request.StaffId, request.StaffName);
            requests.Add(asked);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return ApprovalResultDto.Of(asked);
        }

        var decided = ApprovalRequest.DecidedChange(
            request.RequestId, ApprovalModule.PurchaseInvoice, ApprovalAction.Update, original.Id, summary, changes, payload, request.Reason,
            request.StaffId, request.StaffName);
        requests.Add(decided);
        await corrector.ApplyAsync(original, request.Correction, request.StaffName, request.StaffName, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ApprovalResultDto.Of(decided);
    }
}

// What a correction would change, checked as it will be when sent: the Review changes step. The
// same lines a manager sees on the request.
public sealed record PreviewInvoiceCorrectionQuery(Guid InvoiceId, InvoiceCorrection Correction, string StaffName) : IRequest<InvoiceCorrectionPreview>;

public sealed record InvoiceCorrectionPreview(string Summary, IReadOnlyList<ApprovalChange> Changes);

internal sealed class PreviewInvoiceCorrectionHandler(IApprovalRequestRepository requests, InvoiceCorrector corrector)
    : IRequestHandler<PreviewInvoiceCorrectionQuery, InvoiceCorrectionPreview>
{
    public async Task<InvoiceCorrectionPreview> Handle(PreviewInvoiceCorrectionQuery request, CancellationToken cancellationToken)
    {
        var original = await corrector.OriginalAsync(request.InvoiceId, cancellationToken);
        await requests.EnsureNotPendingAsync(original.Id, cancellationToken);
        var corrected = await corrector.BuildAsync(original, request.Correction, request.StaffName, track: false, cancellationToken);
        var (summary, changes) = await corrector.DescribeAsync(original, corrected, cancellationToken);
        return new InvoiceCorrectionPreview(summary, changes);
    }
}

// Checks, describes and applies a correction: for a manager's own and for an approval alike.
internal sealed class InvoiceCorrector(
    IPurchaseInvoiceRepository invoices,
    InvoiceRecorder recorder,
    InvoicePoster poster,
    DeletionTargets deletions,
    IStockMovementRepository movements,
    IPurchaseAttachmentStore attachments,
    IRawMaterialRepository materials,
    IExpenseItemRepository expenses,
    ISupplierRepository suppliers)
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public Task<PurchaseInvoice> OriginalAsync(Guid invoiceId, CancellationToken cancellationToken) =>
        invoices.GetRequiredAsync(invoiceId, cancellationToken);

    // The correction as a new invoice, checked as one entered and posted would be: only built
    // (track false), before a request goes to a manager; tracked, to be posted.
    public async Task<PurchaseInvoice> BuildAsync(
        PurchaseInvoice original, InvoiceCorrection change, string staffName, bool track, CancellationToken cancellationToken)
    {
        if (original.Status == PurchaseInvoiceStatus.Draft)
            throw new DomainException("This invoice is a draft: finish it instead.");
        if (original.Status == PurchaseInvoiceStatus.Voided)
            throw new DomainException(original.ReplacedBy is null
                ? "This invoice has been voided."
                : "This invoice has already been corrected: correct the invoice that replaced it.");
        var beforeTreasury = original.PaymentMethod is null or PurchasePaymentMethod.BeforeTreasury;
        if (beforeTreasury && change.PaymentMethod != PurchasePaymentMethod.BeforeTreasury)
            throw new DomainException("This invoice is from before the treasury: it is corrected as one, touching neither the safe nor a supplier's account.");
        if (!beforeTreasury && change.PaymentMethod == PurchasePaymentMethod.BeforeTreasury)
            throw new DomainException("Choose how the invoice is paid: from the safe, from a day's takings, or on credit.");

        var corrected = await recorder.CorrectionAsync(
            original,
            new SavePurchaseInvoiceCommand(
                change.NewInvoiceId, change.SupplierId, change.SupplierInvoiceNumber, change.InvoiceDate, change.Lines, staffName,
                change.PaymentMethod, change.RevenueDate),
            track,
            cancellationToken);
        corrected.EnsurePostable();
        if (Same(original, corrected))
            throw new DomainException("Nothing has changed: enter the invoice as it should have been.");
        return corrected;
    }

    // Done: the original voided, replaced by the correction, posted. requestedBy entered it;
    // decidedBy, the manager who did or approved it.
    public async Task ApplyAsync(PurchaseInvoice original, InvoiceCorrection change, string requestedBy, string decidedBy, CancellationToken cancellationToken)
    {
        var corrected = await BuildAsync(original, change, requestedBy, track: true, cancellationToken);
        foreach (var reversal in await deletions.VoidInvoiceAsync(original, decidedBy, corrected.Id, cancellationToken))
            movements.Add(reversal);

        // Its paper, kept with it.
        if (original.HasAttachment && await attachments.GetAsync(original.Id, cancellationToken) is { } paper)
        {
            corrected.Attach(original.AttachmentFileName!, original.AttachmentContentType!, paper.Length, original.AttachedBy ?? decidedBy);
            attachments.Put(corrected.Id, paper);
        }

        await poster.PostAsync(corrected, decidedBy, string.Empty, canApprove: true, cancellationToken);
    }

    // "Invoice F-1 · Nile Farms corrected · L.E 400.00 → L.E 420.00", and what changes, line by line.
    public async Task<(string Summary, IReadOnlyList<ApprovalChange> Changes)> DescribeAsync(
        PurchaseInvoice original, PurchaseInvoice corrected, CancellationToken cancellationToken)
    {
        var supplierIds = new[] { original.SupplierId, corrected.SupplierId }.OfType<Guid>().Distinct().ToList();
        var supplierNames = new Dictionary<Guid, string>();
        foreach (var id in supplierIds)
            supplierNames[id] = (await suppliers.GetByIdAsync(id, cancellationToken))?.Name ?? "A removed supplier";
        string? Supplier(PurchaseInvoice i) => i.SupplierId is { } id ? supplierNames[id] : null;

        var lines = original.Lines.Concat(corrected.Lines).ToList();
        var materialIds = lines.Where(l => l.RawMaterialId != null).Select(l => l.RawMaterialId!.Value).Distinct().ToList();
        var materialNames = materialIds.Count == 0
            ? new Dictionary<Guid, string>()
            : (await materials.GetRequiredByIdsAsync(materialIds, cancellationToken)).ToDictionary(m => m.Key, m => m.Value.Name);
        var expenseIds = lines.Where(l => l.ExpenseItemId != null).Select(l => l.ExpenseItemId!.Value).Distinct().ToList();
        var expenseNames = expenseIds.Count == 0
            ? new Dictionary<Guid, string>()
            : (await expenses.GetByIdsAsync(expenseIds, cancellationToken)).ToDictionary(e => e.Id, e => e.Name);

        var changes = new List<ApprovalChange>();
        void Change(string label, string? before, string? after)
        {
            if (before != after)
                changes.Add(new ApprovalChange(label, before, after));
        }

        Change("Supplier", Supplier(original), Supplier(corrected));
        Change("Invoice no.", original.SupplierInvoiceNumber, corrected.SupplierInvoiceNumber);
        Change("Invoice date", Day(original.InvoiceDate), Day(corrected.InvoiceDate));
        Change("Paid", PaidBy(original), PaidBy(corrected));

        // Each item, as it was and as it is: added, gone, or changed.
        var before = original.Lines.ToDictionary(Key);
        var after = corrected.Lines.ToDictionary(Key);
        foreach (var key in before.Keys.Concat(after.Keys).Distinct())
        {
            var name = key.Material is { } m ? materialNames.GetValueOrDefault(m, "A removed material")
                : key.Expense is { } e ? expenseNames.GetValueOrDefault(e, "A removed expense")
                : "An item";
            Change(name, before.TryGetValue(key, out var was) ? Line(was) : null, after.TryGetValue(key, out var now) ? Line(now) : null);
        }

        changes.Add(new ApprovalChange("Total", Money(original.Total), Money(corrected.Total)));
        var what = (original.SupplierInvoiceNumber is { } number ? $"Invoice {number}" : $"Invoice of {Day(original.InvoiceDate) ?? "no date"}") +
                   (Supplier(original) is { } supplier ? $" · {supplier}" : "");
        return ($"{what} corrected · {Money(original.Total)} → {Money(corrected.Total)}", changes);
    }

    private static bool Same(PurchaseInvoice a, PurchaseInvoice b) =>
        a.SupplierId == b.SupplierId && a.SupplierInvoiceNumber == b.SupplierInvoiceNumber && a.InvoiceDate == b.InvoiceDate
        && (a.PaymentMethod ?? PurchasePaymentMethod.BeforeTreasury) == (b.PaymentMethod ?? PurchasePaymentMethod.BeforeTreasury)
        && a.RevenueDate == b.RevenueDate
        && a.Lines.Count == b.Lines.Count
        && a.Lines.Select(Entry).Order().SequenceEqual(b.Lines.Select(Entry).Order());

    // A line as a value to compare: amounts read back keep their trailing zeros (6.0000), so each is
    // written plainly.
    private static string Entry(PurchaseInvoiceLine l) =>
        string.Join("|", l.ItemType, l.RawMaterialId, l.ExpenseItemId, Plain(l.EnteredQuantity), l.UnitLabel, Plain(l.UnitSize), l.Cost is { } c ? Plain(c) : null);

    private static string Plain(decimal value) => value.ToString("0.##########", Invariant);

    private static (Guid? Material, Guid? Expense) Key(PurchaseInvoiceLine l) => (l.RawMaterialId, l.ExpenseItemId);

    // "5 kg · L.E 100.00".
    private static string Line(PurchaseInvoiceLine l) =>
        $"{l.EnteredQuantity.ToString("0.###", Invariant)}{(string.IsNullOrWhiteSpace(l.UnitLabel) ? "" : " " + l.UnitLabel)} · " +
        (l.Cost is { } cost ? Money(cost) : "no price");

    private static string PaidBy(PurchaseInvoice i) => i.PaymentMethod switch
    {
        PurchasePaymentMethod.Safe => "From the safe",
        PurchasePaymentMethod.DayRevenue => $"From the takings of {Day(i.RevenueDate)}",
        PurchasePaymentMethod.Credit => "On credit",
        _ => "Before the treasury",
    };

    private static string? Day(DateOnly? day) => day?.ToString("d MMM yyyy", Invariant);

    private static string Money(decimal value) => $"L.E {value.ToString("N2", Invariant)}";
}
