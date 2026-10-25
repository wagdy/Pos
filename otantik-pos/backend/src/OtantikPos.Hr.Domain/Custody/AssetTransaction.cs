using Otantik.BuildingBlocks;

namespace OtantikPos.Hr.Domain.Custody;

public enum AssetTransactionType
{
    // إذن دخول: into an employee's custody.
    CheckIn = 1,

    // إذن خروج: out of it, with a reason.
    CheckOut = 2,
}

// Why an asset is checked out.
public enum AssetMovementReason
{
    // Handed back to the store (مرتجع), all of it.
    Returned = 1,

    // Out for repair (صيانة), all of it.
    Maintenance = 2,

    // Broken (كسر): a consumable's breakage is a cost of the day, in Costing → Waste.
    Breakage = 3,

    // Lost (فقد).
    Lost = 4,

    // Thrown away, worn out (استبعاد).
    Discarded = 5,
}

// A custody voucher (إذن): an asset into an employee's custody, or out of it and why, on a
// business day; made by AssetItem's CheckIn and CheckOut. EmployeeId: who took it in, or who held
// it when it went out (none for one going out of the store). The piece's cost as it was then, so
// a voucher's cost does not change with the asset's.
public sealed class AssetTransaction : AggregateRoot
{
    private AssetTransaction() { }

    internal AssetTransaction(
        Guid id,
        Guid assetItemId,
        AssetTransactionType type,
        DateOnly date,
        Guid? employeeId,
        int quantity,
        AssetMovementReason? reason,
        string? notes,
        decimal? unitCost,
        string recordedBy)
    {
        var trimmed = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (trimmed?.Length > AssetItem.MaxNotesLength)
            throw new DomainException($"Keep the notes under {AssetItem.MaxNotesLength} characters.");
        Id = id;
        AssetItemId = assetItemId;
        Type = type;
        Date = date;
        EmployeeId = employeeId;
        Quantity = quantity;
        Reason = reason;
        Notes = trimmed;
        UnitCost = unitCost;
        RecordedBy = recordedBy;
        RecordedAtUtc = DateTime.UtcNow;
    }

    public Guid AssetItemId { get; private set; }
    public AssetTransactionType Type { get; private set; }
    public DateOnly Date { get; private set; }
    public Guid? EmployeeId { get; private set; }
    public int Quantity { get; private set; }

    // A check-out's; none for a check-in.
    public AssetMovementReason? Reason { get; private set; }

    public string? Notes { get; private set; }
    public decimal? UnitCost { get; private set; }
    public string RecordedBy { get; private set; } = string.Empty;
    public DateTime RecordedAtUtc { get; private set; }

    // The breakage entry in Costing → Waste it made, a consumable's breakage.
    public Guid? WasteEntryId { get; private set; }

    public decimal? Cost => UnitCost is { } cost ? cost * Quantity : null;

    public bool RoutedToWaste => WasteEntryId is not null;

    public void RouteToWaste(Guid wasteEntryId)
    {
        if (Reason != AssetMovementReason.Breakage)
            throw new DomainException("Only a breakage goes to Waste.");
        if (WasteEntryId is not null)
            throw new DomainException("This breakage is in Waste already.");
        WasteEntryId = wasteEntryId;
    }
}
