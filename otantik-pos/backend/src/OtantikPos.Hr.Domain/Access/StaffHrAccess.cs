using Otantik.BuildingBlocks;

namespace OtantikPos.Hr.Domain.Access;

// The HR & Payroll tabs a till account that is not a manager's sees: an accountant's. Their
// account is the delivery system's, copied down to the till, so what HR gives them is kept apart
// here, by the account's id. None of these for an account: every tab, as an accountant has always
// had; once a manager ticks theirs, only those.
public sealed class StaffHrAccess : AggregateRoot
{
    public const int MaxStaffIdLength = 450;

    private StaffHrAccess() { }

    public StaffHrAccess(string staffId, HrTabs tabs, string setBy)
    {
        if (string.IsNullOrWhiteSpace(staffId))
            throw new DomainException("Say whose tabs these are.");
        StaffId = staffId.Trim();
        Set(tabs, setBy);
    }

    public string StaffId { get; private set; } = string.Empty;
    public HrTabs Tabs { get; private set; }
    public string SetBy { get; private set; } = string.Empty;
    public DateTime SetAtUtc { get; private set; }

    public void Set(HrTabs tabs, string setBy)
    {
        Tabs = tabs & HrTabs.All;
        SetBy = setBy;
        SetAtUtc = DateTime.UtcNow;
    }
}
