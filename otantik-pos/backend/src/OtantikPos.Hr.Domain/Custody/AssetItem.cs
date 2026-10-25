using Otantik.BuildingBlocks;

namespace OtantikPos.Hr.Domain.Custody;

// Where an asset came from: entered in HR, or bought on a posted purchase invoice (a line of an
// expense item marked as an asset).
public enum AssetOrigin
{
    HrManual = 1,
    FinancialPurchase = 2,
}

public enum AssetStatus
{
    // Not in anyone's custody: in the store, bought and not handed out yet, or handed back.
    InStore = 1,

    // In the custody of its employee (في العهدة).
    InCustody = 2,

    // Out for repair (صيانة), with no one.
    Maintenance = 3,

    // All of it broken (كسر هالك): none left.
    BrokenWaste = 4,

    // All of it lost or thrown away (مستبعد): none left.
    Discarded = 5,
}

// An asset (أصل), or a number of one kind: "Dinner plates × 40", a laptop × 1, at a cost a piece
// when it is known. All of it is with one employee at a time, or with no one. It changes hands
// only by vouchers (AssetTransaction): a check-in (إذن دخول) puts it in an employee's custody; a
// check-out (إذن خروج) takes it out, with why: handed back, to maintenance, or some of it broken,
// lost or thrown away. Nothing is deleted: what is gone stays, with no quantity left.
public sealed class AssetItem : AggregateRoot
{
    public const int MaxNameLength = 150;
    public const int MaxNotesLength = 300;
    public const int MaxQuantity = 100_000;
    public const decimal MaxUnitCost = 10_000_000;

    private AssetItem() { }

    public AssetItem(
        Guid id,
        string name,
        Guid? categoryId,
        decimal? unitCost,
        int quantity,
        AssetOrigin origin,
        string createdBy,
        Guid? sourceInvoiceId = null,
        Guid? sourceInvoiceLineId = null,
        string? notes = null)
    {
        if (!Enum.IsDefined(origin))
            throw new DomainException("Say where the asset came from.");
        if ((origin == AssetOrigin.FinancialPurchase) != (sourceInvoiceId is not null && sourceInvoiceLineId is not null))
            throw new DomainException("An asset bought on an invoice names its invoice and line; one entered in HR, none.");
        if (quantity is < 1 or > MaxQuantity)
            throw new DomainException($"Enter how many there are: 1 to {MaxQuantity:N0}.");
        Id = id;
        Rename(name);
        MoveTo(categoryId);
        SetUnitCost(unitCost);
        SetNotes(notes);
        Quantity = quantity;
        Origin = origin;
        SourceInvoiceId = sourceInvoiceId;
        SourceInvoiceLineId = sourceInvoiceLineId;
        Status = AssetStatus.InStore;
        CreatedBy = createdBy;
        CreatedAtUtc = DateTime.UtcNow;
    }

    public string Name { get; private set; } = string.Empty;
    public Guid? CategoryId { get; private set; }

    // A piece's cost; none when it is not known.
    public decimal? UnitCost { get; private set; }

    // How many there are now: in custody or in the store. Broken, lost or thrown away ones are not.
    public int Quantity { get; private set; }

    // Whose custody it is in (أمين العهدة); none in the store or at maintenance.
    public Guid? EmployeeId { get; private set; }

    public AssetOrigin Origin { get; private set; }
    public AssetStatus Status { get; private set; }

    // The posted invoice and line it was bought on, for a financial one.
    public Guid? SourceInvoiceId { get; private set; }
    public Guid? SourceInvoiceLineId { get; private set; }

    public string? Notes { get; private set; }
    public string CreatedBy { get; private set; } = string.Empty;
    public DateTime CreatedAtUtc { get; private set; }

    public decimal? TotalCost => UnitCost is { } cost ? cost * Quantity : null;

    // None of it is left.
    public bool IsGone => Status is AssetStatus.BrokenWaste or AssetStatus.Discarded;

    public void Rename(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            throw new DomainException("Give the asset its name.");
        if (trimmed.Length > MaxNameLength)
            throw new DomainException($"Keep the asset's name under {MaxNameLength} characters.");
        Name = trimmed;
    }

    public void MoveTo(Guid? categoryId) =>
        CategoryId = categoryId == Guid.Empty ? throw new DomainException("Choose the asset's category, or none.") : categoryId;

    // From now on: the vouchers already made keep the cost they were made at.
    public void SetUnitCost(decimal? unitCost)
    {
        if (unitCost is < 0 or > MaxUnitCost)
            throw new DomainException("A piece's cost is zero or more; check the amount.");
        UnitCost = unitCost is { } cost ? Math.Round(cost, 2, MidpointRounding.AwayFromZero) : null;
    }

    public void SetNotes(string? notes)
    {
        var trimmed = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (trimmed?.Length > MaxNotesLength)
            throw new DomainException($"Keep the notes under {MaxNotesLength} characters.");
        Notes = trimmed;
    }

    // In the custody of `employeeId` (إذن دخول), all of it: from the store, back from maintenance,
    // or from no one. One in someone else's custody is checked out from them first.
    public AssetTransaction CheckIn(Guid transactionId, Guid employeeId, DateOnly date, string? notes, string recordedBy)
    {
        if (CheckInRefusal(employeeId) is { } why)
            throw new DomainException(why);
        EmployeeId = employeeId;
        Status = AssetStatus.InCustody;
        return new AssetTransaction(transactionId, Id, AssetTransactionType.CheckIn, date, employeeId, Quantity, null, notes, UnitCost, recordedBy);
    }

    // Why it cannot go into their custody now; null when it can.
    public string? CheckInRefusal(Guid employeeId) =>
        employeeId == Guid.Empty ? "Choose who takes it into their custody."
        : IsGone ? "None of it is left to hand over."
        : Status != AssetStatus.InCustody ? null
        : EmployeeId == employeeId ? "It is in their custody already."
        : "It is in someone else's custody: check it out from them first.";

    // Out (إذن خروج), and why. Handed back or to maintenance: all of it, from whoever held it. Broken,
    // lost or thrown away: `quantity` of it, the rest staying where it is; with none left, it is
    // broken or discarded, with no one.
    public AssetTransaction CheckOut(
        Guid transactionId, AssetMovementReason reason, int quantity, DateOnly date, string? notes, string recordedBy)
    {
        if (CheckOutRefusal(reason, quantity) is { } why)
            throw new DomainException(why);
        var holder = EmployeeId;
        switch (reason)
        {
            case AssetMovementReason.Returned:
                EmployeeId = null;
                Status = AssetStatus.InStore;
                break;
            case AssetMovementReason.Maintenance:
                EmployeeId = null;
                Status = AssetStatus.Maintenance;
                break;
            default:
                Quantity -= quantity;
                if (Quantity == 0)
                {
                    EmployeeId = null;
                    Status = reason == AssetMovementReason.Breakage ? AssetStatus.BrokenWaste : AssetStatus.Discarded;
                }

                break;
        }

        return new AssetTransaction(transactionId, Id, AssetTransactionType.CheckOut, date, holder, quantity, reason, notes, UnitCost, recordedBy);
    }

    // Why it cannot go out so now; null when it can.
    public string? CheckOutRefusal(AssetMovementReason reason, int quantity)
    {
        if (!Enum.IsDefined(reason))
            return "Say why it goes out: handed back, maintenance, broken, lost or thrown away.";
        if (IsGone)
            return "None of it is left.";
        var all = quantity == Quantity ? null : Quantity == 1 ? "It goes out as a whole." : $"All {Quantity:N0} of them go out together.";
        return reason switch
        {
            AssetMovementReason.Returned => Status == AssetStatus.InStore ? "It is in the store already." : all,
            AssetMovementReason.Maintenance => Status == AssetStatus.Maintenance ? "It is at maintenance already." : all,
            _ => quantity < 1 || quantity > Quantity ? (Quantity == 1 ? "There is 1 of it." : $"Between 1 and {Quantity:N0} of them.") : null,
        };
    }
}
