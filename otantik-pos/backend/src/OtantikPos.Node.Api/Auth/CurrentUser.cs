using System.Security.Claims;
using Otantik.SharedKernel.Authorization;
using Otantik.SharedKernel.Identity;

namespace OtantikPos.Node.Api.Auth;

// The shared kernel's ICurrentUser, read from the till token.
//
// Safe to construct with no request around: the outbox and the cloud listener resolve
// OrderWorkflow, which takes an ICurrentUser, on background threads. Reading it there throws
// rather than returning an empty id, because an audit entry or a void blamed on nobody is
// worse than a loud failure. Neither of those paths reads it today.
internal sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public string UserId => StaffClaims.IdOf(User) ?? throw NoUser();

    public UserRole Role => StaffClaims.RoleOf(User) ?? throw NoUser();

    private ClaimsPrincipal User =>
        accessor.HttpContext?.User is { Identity.IsAuthenticated: true } user ? user : throw NoUser();

    private static InvalidOperationException NoUser() => new("There is no signed-in staff member here.");
}
