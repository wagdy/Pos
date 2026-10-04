using System.Security.Claims;
using Otantik.SharedKernel.Identity;

namespace OtantikPos.Node.Api.Auth;

// What a till token says about who holds it: the delivery system's AppUser id and UserRole
// name, so the audit trail and the permission table mean the same person and the same rights
// here as in the cloud.
//
// Short claim names, read back as they are: AuthenticationSetup turns off the inbound mapping
// that would otherwise rename them to long XML-namespace claim types.
public static class StaffClaims
{
    public const string Id = "sub";
    public const string Name = "name";
    public const string Role = "role";

    public static string? IdOf(ClaimsPrincipal user) => user.FindFirstValue(Id);

    // Null for anything but a role name this build knows. A number is refused too, though
    // Enum.TryParse would take it.
    public static UserRole? RoleOf(ClaimsPrincipal user) =>
        user.FindFirstValue(Role) is { } value
        && Enum.GetNames<UserRole>().Contains(value)
            ? Enum.Parse<UserRole>(value)
            : null;
}
