using MediatR;
using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Application.Approvals;
using OtantikPos.Inventory.Application.Common;
using OtantikPos.Inventory.Application.RawMaterials;
using OtantikPos.Inventory.Application.Treasury;
using OtantikPos.Inventory.Domain.Approvals;
using OtantikPos.Inventory.Domain.Categories;
using OtantikPos.Inventory.Domain.Expenses;
using OtantikPos.Inventory.Domain.Purchases;
using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Inventory.Domain.StockMovements;
using OtantikPos.Inventory.Domain.Suppliers;
using OtantikPos.Inventory.Domain.Treasury;

namespace OtantikPos.Inventory.Application.Purchases;

// Supplier invoices, entered in two steps: saved as a draft while the lines are typed and checked
// (and the paper photographed), then posted, which takes the stock in. InvoiceId is made by the
// till. Staff names are set by the API from who is signed in.

public sealed record PurchaseAttachmentDto(string FileName, string ContentType, int Bytes, DateTime AttachedAtUtc, string? AttachedBy);

// Material: the line's name, the material's or the expense's. Unit is the material's own; an
// expense line has none. Category: what the item is filed under now.
public sealed record PurchaseInvoiceLineDto(
    Guid? RawMaterialId,
    string Material,
    UnitOfMeasure? Unit,
    decimal EnteredQuantity,
    string UnitLabel,
    decimal UnitSize,
    decimal Quantity,
    decimal? Cost,
    PurchaseItemType ItemType,
    Guid? ExpenseItemId,
    Guid? CategoryId,
    string? Category,
    string? CategoryAr);

public sealed record PurchaseInvoiceDto(
    Guid Id,
    PurchaseInvoiceStatus Status,
    Guid? SupplierId,
    string? SupplierName,
    string? SupplierInvoiceNumber,
    DateOnly? InvoiceDate,
    DateTime CreatedAtUtc,
    string CreatedBy,
    DateTime? PostedAtUtc,
    string? PostedBy,
    decimal Total,
    PurchaseAttachmentDto? Attachment,
    IReadOnlyList<PurchaseInvoiceLineDto> Lines,
    // What went into stock, and what was expenses: Total split.
    decimal InventoryTotal,
    decimal ExpenseTotal,
    DateTime? VoidedAtUtc = null,
    string? VoidedBy = null,
    PurchasePaymentMethod? PaymentMethod = null,
    DateOnly? RevenueDate = null,
    InvoicePaymentStatus PaymentStatus = InvoicePaymentStatus.NotPosted);

// What an invoice's lines are named and filed under, read together for a DTO.
internal sealed record InvoiceReading(
    IRawMaterialRepository Materials,
    IExpenseItemRepository Expenses,
    IPurchaseCategoryRepository Categories,
    ISupplierRepository Suppliers,
    ISafeTransactionRepository Safe,
    IApprovalRequestRepository Requests)
{
    public async Task<PurchaseInvoiceDto> DtoAsync(PurchaseInvoice invoice, CancellationToken cancellationToken)
    {
        var materialIds = invoice.Lines.Where(l => l.RawMaterialId is not null).Select(l => l.RawMaterialId!.Value).Distinct().ToList();
        var materials = materialIds.Count == 0
            ? new Dictionary<Guid, RawMaterial>()
            : (await Materials.GetByIdsAsync(materialIds, cancellationToken)).ToDictionary(m => m.Id);
        var expenseIds = invoice.Lines.Where(l => l.ExpenseItemId is not null).Select(l => l.ExpenseItemId!.Value).Distinct().ToList();
        var expenses = expenseIds.Count == 0
            ? new Dictionary<Guid, ExpenseItem>()
            : (await Expenses.GetByIdsAsync(expenseIds, cancellationToken)).ToDictionary(e => e.Id);
        var categories = invoice.Lines.Count == 0
            ? new Dictionary<Guid, PurchaseCategory>()
            : (await Categories.GetAllAsync(cancellationToken)).ToDictionary(c => c.Id);
        var supplier = invoice.SupplierId is { } id ? await Suppliers.GetByIdAsync(id, cancellationToken) : null;
        return Dto(invoice, materials, expenses, categories, supplier) with
        {
            PaymentStatus = await InvoicePayments.StatusAsync(invoice, Safe, Requests, cancellationToken),
        };
    }

    public static PurchaseInvoiceDto Dto(
        PurchaseInvoice invoice,
        IReadOnlyDictionary<Guid, RawMaterial> materials,
        IReadOnlyDictionary<Guid, ExpenseItem> expenses,
        IReadOnlyDictionary<Guid, PurchaseCategory> categories,
        Supplier? supplier) => new(
        invoice.Id, invoice.Status, invoice.SupplierId, supplier?.Name, invoice.SupplierInvoiceNumber, invoice.InvoiceDate,
        invoice.CreatedAtUtc, invoice.CreatedBy, invoice.PostedAtUtc, invoice.PostedBy, invoice.Total,
        invoice.HasAttachment
            ? new PurchaseAttachmentDto(invoice.AttachmentFileName!, invoice.AttachmentContentType!, invoice.AttachmentBytes ?? 0, invoice.AttachedAtUtc!.Value, invoice.AttachedBy)
            : null,
        invoice.Lines.Select(l => Line(l, materials, expenses, categories)).ToList(),
        invoice.InventoryTotal,
        invoice.ExpenseTotal,
        invoice.VoidedAtUtc,
        invoice.VoidedBy,
        invoice.PaymentMethod,
        invoice.RevenueDate);

    private static PurchaseInvoiceLineDto Line(
        PurchaseInvoiceLine l,
        IReadOnlyDictionary<Guid, RawMaterial> materials,
        IReadOnlyDictionary<Guid, ExpenseItem> expenses,
        IReadOnlyDictionary<Guid, PurchaseCategory> categories)
    {
        if (l.ItemType == PurchaseItemType.Expense)
        {
            var expense = expenses.GetValueOrDefault(l.ExpenseItemId!.Value);
            var category = expense is null ? null : categories.GetValueOrDefault(expense.CategoryId);
            return new PurchaseInvoiceLineDto(
                null, expense?.Name ?? "A removed expense", null, l.EnteredQuantity, l.UnitLabel, l.UnitSize, l.Quantity, l.Cost,
                l.ItemType, l.ExpenseItemId, category?.Id, category?.Name, category?.NameAr);
        }

        var material = materials.GetValueOrDefault(l.RawMaterialId!.Value);
        var filed = material is null ? null : categories.GetValueOrDefault(material.CategoryId);
        return new PurchaseInvoiceLineDto(
            l.RawMaterialId, material?.Name ?? "A removed material", material?.Unit ?? UnitOfMeasure.Piece, l.EnteredQuantity, l.UnitLabel,
            l.UnitSize, l.Quantity, l.Cost, l.ItemType, null, filed?.Id, filed?.Name, filed?.NameAr);
    }
}

public sealed record PurchaseInvoiceSummaryDto(
    Guid Id,
    PurchaseInvoiceStatus Status,
    Guid? SupplierId,
    string? SupplierName,
    string? SupplierInvoiceNumber,
    DateOnly? InvoiceDate,
    DateTime CreatedAtUtc,
    string CreatedBy,
    DateTime? PostedAtUtc,
    string? PostedBy,
    decimal Total,
    int Lines,
    // Null while no paper is attached.
    string? AttachmentFileName,
    decimal InventoryTotal,
    decimal ExpenseTotal,
    DateTime? VoidedAtUtc = null,
    string? VoidedBy = null,
    PurchasePaymentMethod? PaymentMethod = null,
    DateOnly? RevenueDate = null,
    InvoicePaymentStatus PaymentStatus = InvoicePaymentStatus.NotPosted);

// Posted invoices newest first, or the drafts.
public sealed record GetPurchaseInvoicesQuery(PurchaseInvoiceStatus Status) : IRequest<IReadOnlyList<PurchaseInvoiceSummaryDto>>;

internal sealed class GetPurchaseInvoicesQueryHandler(
    IPurchaseInvoiceRepository invoices, ISupplierRepository suppliers, ISafeTransactionRepository safe, IApprovalRequestRepository requests)
    : IRequestHandler<GetPurchaseInvoicesQuery, IReadOnlyList<PurchaseInvoiceSummaryDto>>
{
    public async Task<IReadOnlyList<PurchaseInvoiceSummaryDto>> Handle(GetPurchaseInvoicesQuery request, CancellationToken cancellationToken)
    {
        // Archived ones too: an invoice keeps showing who it was from.
        var names = (await suppliers.GetAllAsync(cancellationToken)).ToDictionary(s => s.Id, s => s.Name);
        var listed = await invoices.GetByStatusAsync(request.Status, cancellationToken);
        var paid = await safe.PaidInvoicesAsync(listed.Select(i => i.Id).ToList(), cancellationToken);
        var pending = (await requests.GetPendingAsync(cancellationToken)).Where(r => r.Module == ApprovalModule.Treasury).Select(r => r.RecordId).ToHashSet();
        return listed
            .OrderByDescending(i => i.PostedAtUtc ?? i.CreatedAtUtc)
            .Select(i => new PurchaseInvoiceSummaryDto(
                i.Id, i.Status, i.SupplierId, i.SupplierId is { } id ? names.GetValueOrDefault(id) : null,
                i.SupplierInvoiceNumber, i.InvoiceDate, i.CreatedAtUtc, i.CreatedBy,
                i.PostedAtUtc, i.PostedBy, i.Total, i.Lines.Count, i.AttachmentFileName, i.InventoryTotal, i.ExpenseTotal, i.VoidedAtUtc, i.VoidedBy,
                i.PaymentMethod, i.RevenueDate, InvoicePayments.StatusOf(i, paid.Contains(i.Id), pending.Contains(i.Id))))
            .ToList();
    }
}

public sealed record GetPurchaseInvoiceQuery(Guid InvoiceId) : IRequest<PurchaseInvoiceDto?>;

internal sealed class GetPurchaseInvoiceQueryHandler(
    IPurchaseInvoiceRepository invoices,
    IRawMaterialRepository materials,
    IExpenseItemRepository expenses,
    IPurchaseCategoryRepository categories,
    ISupplierRepository suppliers,
    ISafeTransactionRepository safe,
    IApprovalRequestRepository requests) : IRequestHandler<GetPurchaseInvoiceQuery, PurchaseInvoiceDto?>
{
    public async Task<PurchaseInvoiceDto?> Handle(GetPurchaseInvoiceQuery request, CancellationToken cancellationToken) =>
        await invoices.GetByIdAsync(request.InvoiceId, cancellationToken) is { } invoice
            ? await new InvoiceReading(materials, expenses, categories, suppliers, safe, requests).DtoAsync(invoice, cancellationToken)
            : null;
}

// Step one: the invoice as entered, kept as a draft. Created on its first save.
public sealed record SavePurchaseInvoiceCommand(
    Guid InvoiceId,
    Guid? SupplierId,
    string? SupplierInvoiceNumber,
    DateOnly? InvoiceDate,
    IReadOnlyList<PurchaseInvoiceLineEntry> Lines,
    string StaffName = "",
    PurchasePaymentMethod? PaymentMethod = null,
    DateOnly? RevenueDate = null) : IRequest<PurchaseInvoiceDto>;

internal sealed class SavePurchaseInvoiceCommandHandler(
    IPurchaseInvoiceRepository invoices,
    IRawMaterialRepository materials,
    IExpenseItemRepository expenses,
    IPurchaseCategoryRepository categories,
    ISupplierRepository suppliers,
    ISafeTransactionRepository safe,
    IApprovalRequestRepository requests,
    IUnitOfWork unitOfWork) : IRequestHandler<SavePurchaseInvoiceCommand, PurchaseInvoiceDto>
{
    public async Task<PurchaseInvoiceDto> Handle(SavePurchaseInvoiceCommand request, CancellationToken cancellationToken)
    {
        // A draft can be saved before its lines, to attach its paper first.
        var materialIds = request.Lines
            .Where(l => l.ItemType == PurchaseItemType.Inventory && l.RawMaterialId is not null)
            .Select(l => l.RawMaterialId!.Value)
            .ToList();
        var onInvoice = materialIds.Count == 0
            ? new Dictionary<Guid, RawMaterial>()
            : await materials.GetRequiredByIdsAsync(materialIds, cancellationToken);
        if (onInvoice.Values.FirstOrDefault(m => m.IsPrepItem) is { } made)
            throw new DomainException($"{made.Name} is made in the kitchen, not bought: make it with a production order.");

        var invoice = await invoices.GetByIdAsync(request.InvoiceId, cancellationToken);
        if (invoice is null)
            invoices.Add(invoice = new PurchaseInvoice(request.InvoiceId, request.StaffName));

        // A material deleted since stays on a draft that already had it, but goes on no other.
        var drafted = invoice.Lines.Select(l => l.RawMaterialId).ToHashSet();
        if (onInvoice.Values.FirstOrDefault(m => m.IsArchived && !drafted.Contains(m.Id)) is { } archivedMaterial)
            throw new DomainException($"{archivedMaterial.Name} has been deleted from the raw materials.");

        // An archived expense stays on a draft that already had it, but is not put on another.
        var expenseIds = request.Lines.Where(l => l.ItemType == PurchaseItemType.Expense && l.ExpenseItemId is not null)
            .Select(l => l.ExpenseItemId!.Value).Distinct().ToList();
        if (expenseIds.Count > 0)
        {
            var found = (await expenses.GetByIdsAsync(expenseIds, cancellationToken)).ToDictionary(e => e.Id);
            if (expenseIds.Any(id => !found.ContainsKey(id)))
                throw new DomainException("An expense on this invoice is no longer on the list. Choose it again, or add it.");
            var already = invoice.Lines.Select(l => l.ExpenseItemId).ToHashSet();
            if (found.Values.FirstOrDefault(e => e.IsArchived && !already.Contains(e.Id)) is { } archived)
                throw new DomainException($"{archived.Name} is archived. Restore it on the Expense items tab to put it on an invoice again.");
        }

        // An archived supplier stays on a draft that already had it, but is not put on another.
        if (request.SupplierId is { } supplierId && supplierId != invoice.SupplierId)
        {
            var supplier = await suppliers.GetByIdAsync(supplierId, cancellationToken)
                ?? throw new DomainException("That supplier is no longer on the list. Choose it again, or add it.");
            if (supplier.IsArchived)
                throw new DomainException($"{supplier.Name} is archived. Restore it on the Suppliers tab to invoice it again.");
        }

        invoice.Record(request.SupplierId, request.SupplierInvoiceNumber, request.InvoiceDate, request.Lines, request.PaymentMethod, request.RevenueDate);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await new InvoiceReading(materials, expenses, categories, suppliers, safe, requests).DtoAsync(invoice, cancellationToken);
    }
}

// Step two: confirmed, the stock goes in and the average costs move; expense lines are recorded
// as they are, touching no stock. Its payment is settled as chosen: on credit, owed to the
// supplier; in cash, paid for a manager and asked of one for anyone else (the stock is in either
// way). Posting again, after an answer was lost, returns the posted invoice and changes nothing.
public sealed record PostPurchaseInvoiceCommand(Guid InvoiceId, string StaffName = "", string StaffId = "", bool CanApprove = true)
    : IRequest<PurchaseInvoiceDto>, IRetryOnConflict;

internal sealed class PostPurchaseInvoiceCommandHandler(
    IPurchaseInvoiceRepository invoices,
    IRawMaterialRepository materials,
    IExpenseItemRepository expenses,
    IPurchaseCategoryRepository categories,
    ISupplierRepository suppliers,
    IStockMovementRepository movements,
    ISafeTransactionRepository safe,
    IApprovalRequestRepository requests,
    TreasuryWriter treasury,
    IUnitOfWork unitOfWork) : IRequestHandler<PostPurchaseInvoiceCommand, PurchaseInvoiceDto>
{
    public async Task<PurchaseInvoiceDto> Handle(PostPurchaseInvoiceCommand request, CancellationToken cancellationToken)
    {
        var reading = new InvoiceReading(materials, expenses, categories, suppliers, safe, requests);
        var invoice = await invoices.GetRequiredAsync(request.InvoiceId, cancellationToken);
        if (invoice.Status == PurchaseInvoiceStatus.Posted)
            return await reading.DtoAsync(invoice, cancellationToken);

        var materialIds = invoice.Lines.Where(l => l.RawMaterialId is not null).Select(l => l.RawMaterialId!.Value).ToList();
        IReadOnlyDictionary<Guid, RawMaterial> byId = materialIds.Count == 0
            ? new Dictionary<Guid, RawMaterial>()
            : await materials.GetRequiredByIdsAsync(materialIds, cancellationToken);
        foreach (var movement in invoice.Post(byId, request.StaffName))
            movements.Add(movement);
        await treasury.SettleAsync(invoice, Guid.NewGuid(), request.StaffId, request.StaffName, request.CanApprove, requests, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await reading.DtoAsync(invoice, cancellationToken);
    }
}

public sealed record DiscardPurchaseInvoiceCommand(Guid InvoiceId) : IRequest;

internal sealed class DiscardPurchaseInvoiceCommandHandler(IPurchaseInvoiceRepository invoices, IUnitOfWork unitOfWork)
    : IRequestHandler<DiscardPurchaseInvoiceCommand>
{
    public async Task Handle(DiscardPurchaseInvoiceCommand request, CancellationToken cancellationToken)
    {
        // Already gone: a retry of a discard that worked.
        if (await invoices.GetByIdAsync(request.InvoiceId, cancellationToken) is not { } invoice)
            return;
        if (invoice.Status == PurchaseInvoiceStatus.Posted)
            throw new DomainException("A posted invoice stays: it is the record of what came in.");

        invoices.Remove(invoice);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

// The paper: a photo or scan of the invoice. A draft is made for it if there is none yet.
public sealed record AttachPurchaseInvoiceFileCommand(Guid InvoiceId, string FileName, string ContentType, byte[] Content, string StaffName = "")
    : IRequest<PurchaseInvoiceDto>;

internal sealed class AttachPurchaseInvoiceFileCommandHandler(
    IPurchaseInvoiceRepository invoices,
    IPurchaseAttachmentStore attachments,
    IRawMaterialRepository materials,
    IExpenseItemRepository expenses,
    IPurchaseCategoryRepository categories,
    ISupplierRepository suppliers,
    ISafeTransactionRepository safe,
    IApprovalRequestRepository requests,
    IUnitOfWork unitOfWork) : IRequestHandler<AttachPurchaseInvoiceFileCommand, PurchaseInvoiceDto>
{
    public async Task<PurchaseInvoiceDto> Handle(AttachPurchaseInvoiceFileCommand request, CancellationToken cancellationToken)
    {
        var invoice = await invoices.GetByIdAsync(request.InvoiceId, cancellationToken);
        if (invoice is null)
            invoices.Add(invoice = new PurchaseInvoice(request.InvoiceId, request.StaffName));

        if (!LooksLike(request.ContentType, request.Content))
            throw new DomainException("That file is not the photo or PDF it says it is.");
        invoice.Attach(request.FileName, request.ContentType, request.Content.Length, request.StaffName);
        attachments.Put(invoice.Id, request.Content);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await new InvoiceReading(materials, expenses, categories, suppliers, safe, requests).DtoAsync(invoice, cancellationToken);
    }

    // Checked, not taken on trust: the file is shown in the browser later, so it must be the
    // picture or PDF its type says, by its first bytes.
    private static bool LooksLike(string contentType, byte[] content)
    {
        bool StartsWith(params byte[] prefix) => content.Length >= prefix.Length && content.AsSpan(0, prefix.Length).SequenceEqual(prefix);
        return contentType.ToLowerInvariant() switch
        {
            "image/jpeg" => StartsWith(0xFF, 0xD8, 0xFF),
            "image/png" => StartsWith(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A),
            "image/webp" => content.Length >= 12 && StartsWith((byte)'R', (byte)'I', (byte)'F', (byte)'F')
                && content.AsSpan(8, 4).SequenceEqual("WEBP"u8),
            "application/pdf" => StartsWith((byte)'%', (byte)'P', (byte)'D', (byte)'F'),
            _ => false,
        };
    }
}

public sealed record PurchaseAttachmentFile(string FileName, string ContentType, byte[] Content);

public sealed record GetPurchaseInvoiceAttachmentQuery(Guid InvoiceId) : IRequest<PurchaseAttachmentFile?>;

internal sealed class GetPurchaseInvoiceAttachmentQueryHandler(IPurchaseInvoiceRepository invoices, IPurchaseAttachmentStore attachments)
    : IRequestHandler<GetPurchaseInvoiceAttachmentQuery, PurchaseAttachmentFile?>
{
    public async Task<PurchaseAttachmentFile?> Handle(GetPurchaseInvoiceAttachmentQuery request, CancellationToken cancellationToken)
    {
        if (await invoices.GetByIdAsync(request.InvoiceId, cancellationToken) is not { HasAttachment: true } invoice)
            return null;
        return await attachments.GetAsync(invoice.Id, cancellationToken) is { } content
            ? new PurchaseAttachmentFile(invoice.AttachmentFileName!, invoice.AttachmentContentType!, content)
            : null;
    }
}
