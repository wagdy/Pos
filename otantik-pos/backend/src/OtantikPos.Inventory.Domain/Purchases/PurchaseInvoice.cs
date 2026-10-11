using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Inventory.Domain.StockMovements;

namespace OtantikPos.Inventory.Domain.Purchases;

public enum PurchaseInvoiceStatus
{
    // Being entered and checked: no stock has moved.
    Draft = 1,

    // Confirmed: its stock is in, its prices are in the average costs. Fixed from then on.
    Posted = 2,
}

// What a line of an invoice is.
public enum PurchaseItemType
{
    // A raw material: posting takes it into stock and moves its average cost.
    Inventory = 1,

    // Not stock (cleaning supplies, a repair, a bill): posting records only what it cost, under
    // its category, for the profit and loss.
    Expense = 2,
}

// A supplier's invoice as the manager enters it: who from, their number and date, each line as it
// is written on the paper (5 kg, L.E 100), and a photo or scan of the paper itself. Saved as a
// draft while it is entered and reviewed; posting it takes the stock in, each line at the grams,
// millilitres or pieces it comes to, and moves each material's average cost. Expense lines, on the
// same invoice or one of their own, leave stock alone: they are only what they cost.
//
// Its id is made by the till and is the stock movements' SourceId, so a retry after a lost
// answer posts it once.
public sealed class PurchaseInvoice : AggregateRoot
{
    public const int MaxAttachmentBytes = 10 * 1024 * 1024;

    // What a photo or scan of an invoice can be.
    public static readonly IReadOnlySet<string> AttachmentTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/png", "image/webp", "application/pdf" };

    private readonly List<PurchaseInvoiceLine> _lines = [];

    private PurchaseInvoice() { }

    public PurchaseInvoice(Guid id, string createdBy)
    {
        Id = id;
        CreatedBy = createdBy;
        CreatedAtUtc = DateTime.UtcNow;
        Status = PurchaseInvoiceStatus.Draft;
    }

    public PurchaseInvoiceStatus Status { get; private set; }
    // Who it is from: one of the suppliers, or none on a delivery recorded before suppliers were kept.
    public Guid? SupplierId { get; private set; }
    public string? SupplierInvoiceNumber { get; private set; }
    public DateOnly? InvoiceDate { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public string CreatedBy { get; private set; } = string.Empty;
    public DateTime? PostedAtUtc { get; private set; }
    public string? PostedBy { get; private set; }

    // The attached photo or scan, described; its bytes are kept apart.
    public string? AttachmentFileName { get; private set; }
    public string? AttachmentContentType { get; private set; }
    public int? AttachmentBytes { get; private set; }
    public DateTime? AttachedAtUtc { get; private set; }
    public string? AttachedBy { get; private set; }
    public bool HasAttachment => AttachmentFileName is not null;

    public IReadOnlyList<PurchaseInvoiceLine> Lines => _lines.AsReadOnly();

    // What the lines with a price came to: in all, for stock, and for expenses.
    public decimal Total => _lines.Sum(l => l.Cost ?? 0);
    public decimal InventoryTotal => _lines.Where(l => l.ItemType == PurchaseItemType.Inventory).Sum(l => l.Cost ?? 0);
    public decimal ExpenseTotal => _lines.Where(l => l.ItemType == PurchaseItemType.Expense).Sum(l => l.Cost ?? 0);

    // Replaces the draft's details and lines. Each line is as written on the invoice: a quantity
    // in a unit that holds UnitSize of the material's own (5 in kg of 1000 g), or of an expense.
    public void Record(Guid? supplierId, string? supplierInvoiceNumber, DateOnly? invoiceDate, IReadOnlyCollection<PurchaseInvoiceLineEntry> lines)
    {
        if (Status == PurchaseInvoiceStatus.Posted)
            throw new DomainException("This invoice has been posted. Its stock is in; enter a new invoice for another delivery.");
        var materials = lines.Where(l => l.ItemType == PurchaseItemType.Inventory).Select(l => l.RawMaterialId).ToList();
        if (materials.Distinct().Count() != materials.Count)
            throw new DomainException("Each material goes on the invoice once: add its quantities together.");
        var expenses = lines.Where(l => l.ItemType == PurchaseItemType.Expense).Select(l => l.ExpenseItemId).ToList();
        if (expenses.Distinct().Count() != expenses.Count)
            throw new DomainException("Each expense goes on the invoice once: add its amounts together.");
        if (supplierInvoiceNumber?.Trim().Length > 64)
            throw new DomainException("Keep the supplier's invoice number under 64 characters.");

        SupplierId = supplierId;
        SupplierInvoiceNumber = Clean(supplierInvoiceNumber);
        InvoiceDate = invoiceDate;
        _lines.Clear();
        _lines.AddRange(lines.Select(l => new PurchaseInvoiceLine(Id, l)));
    }

    // Takes the stock in and returns the ledger entries, for the caller to add in the same unit of
    // work: each material line at its quantity in the material's own unit, its cost moving the
    // average. Expense lines take nothing in: posted, what they cost is the record.
    public IReadOnlyList<StockMovement> Post(IReadOnlyDictionary<Guid, RawMaterial> materials, string postedBy)
    {
        if (Status == PurchaseInvoiceStatus.Posted)
            throw new DomainException("This invoice has already been posted.");
        if (_lines.Count == 0)
            throw new DomainException("Put at least one line on the invoice before posting it.");

        var movements = _lines
            .Where(l => l.ItemType == PurchaseItemType.Inventory)
            .Select(l => (materials.GetValueOrDefault(l.RawMaterialId!.Value) ?? throw new NotFoundException(nameof(RawMaterial), l.RawMaterialId))
                .ReceivePurchase(l.Quantity, Id, l.Cost))
            .ToList();
        Status = PurchaseInvoiceStatus.Posted;
        PostedAtUtc = DateTime.UtcNow;
        PostedBy = postedBy;
        return movements;
    }

    // The paper, photographed or scanned. On a draft it can be replaced; once the invoice is
    // posted, a missing one can still be added, but what is attached stays, for the audit.
    public void Attach(string fileName, string contentType, int bytes, string attachedBy)
    {
        if (Status == PurchaseInvoiceStatus.Posted && HasAttachment)
            throw new DomainException("This posted invoice already has its paper attached; it is kept as it is.");
        if (!AttachmentTypes.Contains(contentType))
            throw new DomainException("Attach a photo (JPEG, PNG or WebP) or a PDF of the invoice.");
        if (bytes is <= 0 or > MaxAttachmentBytes)
            throw new DomainException("The file must be under 10 MB.");

        AttachmentFileName = Clean(Path.GetFileName(fileName)) is { } name ? name[..Math.Min(name.Length, 200)] : "invoice";
        AttachmentContentType = contentType.ToLowerInvariant();
        AttachmentBytes = bytes;
        AttachedAtUtc = DateTime.UtcNow;
        AttachedBy = attachedBy;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

// A line as the manager enters it: Quantity in the unit chosen (UnitLabel), which holds UnitSize
// of the material's own unit; Cost is what the line came to, when the invoice gives it. An
// expense line names its ExpenseItemId instead of a material, is in its own units (UnitSize 1),
// and always has its cost: it is nothing else.
public sealed record PurchaseInvoiceLineEntry(
    Guid? RawMaterialId,
    decimal EnteredQuantity,
    string? UnitLabel,
    decimal UnitSize,
    decimal? Cost,
    PurchaseItemType ItemType = PurchaseItemType.Inventory,
    Guid? ExpenseItemId = null);

public sealed class PurchaseInvoiceLine : Entity
{
    private PurchaseInvoiceLine() { }

    internal PurchaseInvoiceLine(Guid invoiceId, PurchaseInvoiceLineEntry entry)
    {
        if (entry.EnteredQuantity <= 0)
            throw new DomainException("Each line needs a quantity greater than zero.");
        if (entry.Cost < 0)
            throw new DomainException("A line cannot cost less than nothing.");
        if (entry.UnitLabel?.Trim().Length > 32)
            throw new DomainException("Keep each line's unit under 32 characters.");

        InvoiceId = invoiceId;
        ItemType = entry.ItemType;
        EnteredQuantity = entry.EnteredQuantity;
        Cost = entry.Cost;
        switch (entry.ItemType)
        {
            case PurchaseItemType.Inventory:
                if (entry.RawMaterialId is not { } materialId || materialId == Guid.Empty || entry.ExpenseItemId is not null)
                    throw new DomainException("Choose the material for each stock line.");
                if (entry.UnitSize <= 0)
                    throw new DomainException("Choose the unit each line is in.");
                RawMaterialId = materialId;
                UnitLabel = string.IsNullOrWhiteSpace(entry.UnitLabel) ? "unit" : entry.UnitLabel.Trim();
                UnitSize = entry.UnitSize;
                // To the thousandth, as stock is kept: 0.1 kg is 100 g.
                Quantity = Math.Round(entry.EnteredQuantity * entry.UnitSize, 3, MidpointRounding.AwayFromZero);
                break;
            case PurchaseItemType.Expense:
                if (entry.ExpenseItemId is not { } expenseId || expenseId == Guid.Empty || entry.RawMaterialId is not null)
                    throw new DomainException("Choose the expense for each expense line.");
                if (entry.Cost is null)
                    throw new DomainException("Give each expense line its amount.");
                ExpenseItemId = expenseId;
                UnitLabel = string.IsNullOrWhiteSpace(entry.UnitLabel) ? "unit" : entry.UnitLabel.Trim();
                UnitSize = 1;
                Quantity = Math.Round(entry.EnteredQuantity, 3, MidpointRounding.AwayFromZero);
                break;
            default:
                throw new DomainException("Say whether each line is stock or an expense.");
        }
    }

    public Guid InvoiceId { get; private set; }
    public PurchaseItemType ItemType { get; private set; } = PurchaseItemType.Inventory;

    // One or the other, as ItemType says.
    public Guid? RawMaterialId { get; private set; }
    public Guid? ExpenseItemId { get; private set; }

    // As written on the invoice: 5, in kg.
    public decimal EnteredQuantity { get; private set; }
    public string UnitLabel { get; private set; } = string.Empty;
    public decimal UnitSize { get; private set; }

    // What it comes to in the material's own unit: 5,000 g. An expense's is as entered.
    public decimal Quantity { get; private set; }

    public decimal? Cost { get; private set; }
}
