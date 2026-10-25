using System.Globalization;
using System.Security.Claims;

namespace OtantikPos.Node.Api.Auth;

// What an employee portal token says: the employee's id (sub, as a staff token's), the role
// Employee (never a till role, so no staff permission is ever granted to it), and when their PIN
// was set, so a new PIN, or none, signs them out at once.
public static class PortalClaims
{
    public const string Role = "Employee";
    public const string Policy = "Employee";
    public const string PinSetAt = "pin_set";

    public static bool IsEmployee(ClaimsPrincipal user) => user.FindFirstValue(StaffClaims.Role) == Role;

    public static Guid? EmployeeIdOf(ClaimsPrincipal user) =>
        IsEmployee(user) && Guid.TryParse(user.FindFirstValue(StaffClaims.Id), out var id) ? id : null;

    public static long? PinSetAtOf(ClaimsPrincipal user) =>
        long.TryParse(user.FindFirstValue(PinSetAt), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ? ticks : null;
}
