namespace Otantik.SharedKernel.Authorization;

// The signed-in staff member, as each API reads it from its own token. The delivery system's
// tokens already carry both values: the AppUser id and the UserRole claim.
public interface ICurrentUser
{
    string UserId { get; }

    UserRole Role { get; }
}

public static class CurrentUserExtensions
{
    public static bool Has(this ICurrentUser user, string permission) => RolePermissions.Has(user.Role, permission);
}
