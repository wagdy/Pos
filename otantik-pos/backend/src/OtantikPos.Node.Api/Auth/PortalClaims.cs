using System.Globalization;
using System.Security.Claims;
using OtantikPos.Hr.Domain.Access;

namespace OtantikPos.Node.Api.Auth;

// What an employee portal token says: the employee's id (sub, as a staff token's), the role
// Employee (never a till role, so no staff permission is ever granted to it), when their PIN was
// set, so a new PIN, or none, signs them out at once, and the HR & Payroll tabs a manager gave
// them, a claim each, checked against the ones they have now on every request.
public static class PortalClaims
{
    public const string Role = "Employee";
    public const string Policy = "Employee";
    public const string PinSetAt = "pin_set";
    public const string HrTabsClaim = "hr_tabs";

    public static bool IsEmployee(ClaimsPrincipal user) => user.FindFirstValue(StaffClaims.Role) == Role;

    public static Guid? EmployeeIdOf(ClaimsPrincipal user) =>
        IsEmployee(user) && Guid.TryParse(user.FindFirstValue(StaffClaims.Id), out var id) ? id : null;

    // None when it names none, or is not an employee's; a name that is not a tab is left out.
    public static HrTabs HrTabsOf(ClaimsPrincipal user) =>
        !IsEmployee(user)
            ? HrTabs.None
            : HrTabSet.Join(user.FindAll(HrTabsClaim).Select(c => Enum.TryParse<HrTabs>(c.Value, out var tab) && HrTabSet.Each.Contains(tab) ? tab : HrTabs.None));

    public static long? PinSetAtOf(ClaimsPrincipal user) =>
        long.TryParse(user.FindFirstValue(PinSetAt), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ? ticks : null;
}
