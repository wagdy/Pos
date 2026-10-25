using Otantik.BuildingBlocks;

namespace OtantikPos.Inventory.Domain.Approvals;

// The records a manager decides about.
public enum ApprovalModule
{
    // Soft-deleted: kept, off the lists and out of the sales.
    Recipe = 1,

    // A posted invoice, voided: its goods taken back out of stock, its expenses out of the reports.
    PurchaseInvoice = 2,

    // A waste (spoilage) entry, voided: what it took from stock put back.
    Waste = 3,

    // A posted stock count, voided: its adjustments undone.
    StockCount = 4,

    // Archived: off the lists, kept for the records that name it.
    RawMaterial = 5,

    // Added or changed (its details and its batch recipe), or archived as a raw material is.
    PrepItem = 6,

    // Made (the ingredients out, the prep item in), corrected (the batch reversed and made again)
    // or voided (reversed).
    ProductionOrder = 7,

    // A safe entry: opening balance, funding, a day's takings moved in, an expense voucher, a
    // supplier payment, or an invoice paid in cash. Entered when approved, or voided (reversed).
    Treasury = 8,

    // Deleted, what was in it moved to another category, and on the way its raw materials made
    // expense items or its expense items raw materials, as the request holds.
    PurchaseCategory = 9,

    // HR: an employee added, changed, leaving or coming back.
    Employee = 10,

    // HR: an advance paid outside the till, recorded in HR only.
    Advance = 11,

    // Custody & Assets: an asset added or changed, or a custody voucher (in or out).
    Custody = 12,
}

public enum ApprovalAction
{
    Create = 1,
    Update = 2,
    Delete = 3,
}

public enum ApprovalStatus
{
    // Waiting for a manager. The record is as it was (a new one does not exist yet), counts as
    // before, and cannot be changed.
    Pending = 1,

    // Done: deleted, added or changed.
    Approved = 2,

    // Left as it was.
    Rejected = 3,
}

// One line of what a request changes, as the manager reads it: "Quantity to produce: 10 kg → 12 kg".
// Before is empty for something new, After for something taken away.
public sealed record ApprovalChange(string Label, string? Before, string? After);

// One change asked for and decided: the maker-checker record. Someone without approval rights
// asks; nothing changes while it waits; a manager approves (and it is done) or rejects (and it is
// not). A manager's own delete or correction is recorded too, approved as it is made. Kept
// either way, as the history of who asked for what and who decided.
//
// A deletion needs only the record. A new or changed prep item or production order also holds
// what was entered (Payload, as the module writes it), applied on approval and only then: a new
// one does not exist until it is approved, and a changed one stays as it was. A category's
// deletion holds where its contents go and what they become.
//
// Its id is made by the till, so a retried request is recorded once.
public sealed class ApprovalRequest : AggregateRoot
{
    private List<ApprovalChange> _changes = [];

    private ApprovalRequest() { }

    private ApprovalRequest(
        Guid id,
        ApprovalModule module,
        ApprovalAction action,
        Guid recordId,
        string summary,
        IReadOnlyList<ApprovalChange> changes,
        string? payload,
        string? reason,
        string requestedById,
        string requestedBy)
    {
        if (!Enum.IsDefined(module) || !Enum.IsDefined(action))
            throw new DomainException("Say what is to be done.");
        if (action != ApprovalAction.Delete
            && module is not (ApprovalModule.PrepItem or ApprovalModule.ProductionOrder or ApprovalModule.Treasury or ApprovalModule.PurchaseInvoice
                or ApprovalModule.RawMaterial or ApprovalModule.Employee or ApprovalModule.Advance or ApprovalModule.Custody))
            throw new DomainException(
                "Only prep items, production orders, safe entries, invoices, raw materials, employees and advances are added or changed through an approval.");
        if (action == ApprovalAction.Update && module == ApprovalModule.Treasury)
            throw new DomainException("A safe entry is not changed: void it and enter it again.");
        // An invoice is only corrected through one, and a raw material's unit changed: a new one
        // of either is entered as it is.
        if (action == ApprovalAction.Create && module is (ApprovalModule.PurchaseInvoice or ApprovalModule.RawMaterial))
            throw new DomainException("A new one is entered as it is, not asked for.");
        if (action != ApprovalAction.Delete && string.IsNullOrWhiteSpace(payload))
            throw new DomainException("Say what the change is.");
        if (string.IsNullOrWhiteSpace(summary))
            throw new DomainException("Describe what the request is about.");
        if (reason?.Trim().Length > 500)
            throw new DomainException("Keep the reason under 500 characters.");

        Id = id;
        Module = module;
        Action = action;
        RecordId = recordId;
        Summary = summary.Trim();
        _changes = [.. changes];
        Payload = payload;
        Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        RequestedById = requestedById;
        RequestedBy = requestedBy;
        RequestedAtUtc = DateTime.UtcNow;
        Status = ApprovalStatus.Pending;
    }

    // A deletion, asked for.
    public static ApprovalRequest Ask(
        Guid id, ApprovalModule module, Guid recordId, string summary, string? reason, string requestedById, string requestedBy) =>
        new(id, module, ApprovalAction.Delete, recordId, summary, [], null, reason, requestedById, requestedBy);

    // A new or changed record, asked for, with what was entered.
    public static ApprovalRequest AskToChange(
        Guid id,
        ApprovalModule module,
        ApprovalAction action,
        Guid recordId,
        string summary,
        IReadOnlyList<ApprovalChange> changes,
        string payload,
        string? reason,
        string requestedById,
        string requestedBy) =>
        new(id, module, action, recordId, summary, changes, payload, reason, requestedById, requestedBy);

    // A manager doing it themselves: asked and approved at once, by them.
    public static ApprovalRequest Decided(
        Guid id, ApprovalModule module, Guid recordId, string summary, string? reason, string managerId, string manager)
    {
        var request = new ApprovalRequest(id, module, ApprovalAction.Delete, recordId, summary, [], null, reason, managerId, manager);
        request.Approve(manager);
        return request;
    }

    public static ApprovalRequest DecidedChange(
        Guid id,
        ApprovalModule module,
        ApprovalAction action,
        Guid recordId,
        string summary,
        IReadOnlyList<ApprovalChange> changes,
        string payload,
        string? reason,
        string managerId,
        string manager)
    {
        var request = new ApprovalRequest(id, module, action, recordId, summary, changes, payload, reason, managerId, manager);
        request.Approve(manager);
        return request;
    }

    public ApprovalModule Module { get; private set; }
    public ApprovalAction Action { get; private set; }

    // The record's id in its module: the recipe, invoice, waste entry, count, material, prep item
    // or production order. For a new one, the id it is given when approved.
    public Guid RecordId { get; private set; }

    // What it was when asked about ("Invoice F-1001 · Nile Farms · L.E 1,850.00"), so the
    // decision is made on what was asked and the history reads after the record is gone.
    public string Summary { get; private set; } = string.Empty;

    // What a new or changed record would be, field by field, against what it is now.
    public IReadOnlyList<ApprovalChange> Changes => _changes.AsReadOnly();

    // What was entered, as the module reads it back on approval. Null for a deletion.
    public string? Payload { get; private set; }

    public string? Reason { get; private set; }

    public string RequestedById { get; private set; } = string.Empty;
    public string RequestedBy { get; private set; } = string.Empty;
    public DateTime RequestedAtUtc { get; private set; }

    public ApprovalStatus Status { get; private set; }
    public string? DecidedBy { get; private set; }
    public DateTime? DecidedAtUtc { get; private set; }

    // Why a rejection was made, for the one who asked.
    public string? DecisionNote { get; private set; }

    public bool IsPending => Status == ApprovalStatus.Pending;

    // The caller does what was asked in the same unit of work.
    public void Approve(string decidedBy)
    {
        if (Status == ApprovalStatus.Approved)
            return;
        if (Status != ApprovalStatus.Pending)
            throw new DomainException("This request was rejected; ask again.");
        Status = ApprovalStatus.Approved;
        DecidedBy = decidedBy;
        DecidedAtUtc = DateTime.UtcNow;
    }

    public void Reject(string decidedBy, string? note)
    {
        if (Status == ApprovalStatus.Rejected)
            return;
        if (Status != ApprovalStatus.Pending)
            throw new DomainException("This has already been approved.");
        if (note?.Trim().Length > 500)
            throw new DomainException("Keep the note under 500 characters.");
        Status = ApprovalStatus.Rejected;
        DecidedBy = decidedBy;
        DecidedAtUtc = DateTime.UtcNow;
        DecisionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    }
}

public interface IApprovalRequestRepository : IRepository<ApprovalRequest>
{
    // Waiting for a decision, oldest first.
    Task<IReadOnlyList<ApprovalRequest>> GetPendingAsync(CancellationToken cancellationToken = default);

    // Decided, newest first.
    Task<IReadOnlyList<ApprovalRequest>> GetDecidedAsync(int take, CancellationToken cancellationToken = default);

    // The one waiting for this record, whatever it asks: at most one does.
    Task<ApprovalRequest?> GetPendingForAsync(Guid recordId, CancellationToken cancellationToken = default);
}
