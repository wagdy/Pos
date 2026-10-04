using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Otantik.SharedKernel.Authorization;
using Otantik.SharedKernel.Identity;
using OtantikPos.Node.Api.Auth;
using OtantikPos.Node.Infrastructure.Common;
using OtantikPos.Node.Infrastructure.Identity;

namespace OtantikPos.Node.Api.Controllers;

// PIN sign-in at the till. Works with the internet down: the accounts are the delivery
// system's, copied down by the reference-data sync, and the PINs live only on this machine.
[ApiController]
[Route("api/auth")]
public sealed class AuthController(StaffDirectory staff, TokenService tokens, RestaurantClock clock) : ControllerBase
{
    public const string SignInRateLimit = "sign-in";

    // The sign-in screen: tap your name, then enter your PIN. Names and ids only, and only
    // cashiers, managers and admins who have a PIN. Captains take orders in the delivery app.
    [AllowAnonymous]
    [HttpGet("staff")]
    public async Task<IReadOnlyList<SignInOption>> GetSignInList(CancellationToken cancellationToken) =>
        (await staff.GetSignInListAsync(cancellationToken)).Select(s => new SignInOption(s.Id, s.FullName)).ToList();

    [AllowAnonymous]
    [EnableRateLimiting(SignInRateLimit)]
    [HttpPost("sign-in")]
    public async Task<ActionResult<SignInResponse>> SignIn(SignInRequest request, CancellationToken cancellationToken)
    {
        var outcome = await staff.SignInAsync(request.StaffId, request.Pin, cancellationToken);

        if (outcome.Staff is null)
        {
            var detail = outcome.LockedUntilUtc is { } lockedUntil
                ? $"Too many wrong PINs. Try again after {clock.ToLocal(lockedUntil):HH:mm}."
                : "Wrong PIN.";
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Sign-in failed.", detail: detail);
        }

        var (token, expiresAtUtc) = tokens.Issue(outcome.Staff);
        return new SignInResponse(token, expiresAtUtc, Me.For(outcome.Staff.Id, outcome.Staff.FullName, outcome.Staff.Role));
    }

    // Who is signed in, and what they may do: the permissions come from the shared
    // RolePermissions table, so the Angular app shows and hides buttons by the same rules the
    // API enforces, and both stay right when a role's rights change.
    [HttpGet("me")]
    public Me GetMe() =>
        Me.For(StaffClaims.IdOf(User)!, User.FindFirst(StaffClaims.Name)?.Value ?? string.Empty, StaffClaims.RoleOf(User)!.Value);
}

public sealed record SignInOption(string Id, string FullName);

public sealed record SignInRequest(string StaffId, string Pin);

public sealed record SignInResponse(string Token, DateTime ExpiresAtUtc, Me Staff);

public sealed record Me(string Id, string FullName, UserRole Role, IReadOnlyList<string> Permissions)
{
    public static Me For(string id, string fullName, UserRole role) =>
        new(id, fullName, role, RolePermissions.For(role).Order(StringComparer.Ordinal).ToList());
}
