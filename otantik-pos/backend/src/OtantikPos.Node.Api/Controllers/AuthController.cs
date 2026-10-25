using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Otantik.SharedKernel.Authorization;
using Otantik.SharedKernel.Identity;
using OtantikPos.Hr.Domain.Access;
using OtantikPos.Node.Api.Auth;
using OtantikPos.Node.Infrastructure.Common;
using OtantikPos.Node.Infrastructure.Identity;

namespace OtantikPos.Node.Api.Controllers;

// PIN sign-in at the till. Works with the internet down: the accounts are the delivery
// system's, copied down by the reference-data sync, and the PINs live only on this machine.
[ApiController]
[Route("api/auth")]
public sealed class AuthController(StaffDirectory staff, TokenService tokens, RestaurantClock clock, ISender sender) : ControllerBase
{
    public const string SignInRateLimit = "sign-in";
    public const string EmployeeSignInRateLimit = "employee-sign-in";

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

    // The employee portal's sign-in: their mobile number and the PIN a manager set. A token with
    // the role Employee, good for the portal's own endpoints only. One answer for a number nobody
    // has and a wrong PIN, so it does not say whose numbers are known.
    [AllowAnonymous]
    [EnableRateLimiting(EmployeeSignInRateLimit)]
    [HttpPost("employee-login")]
    public async Task<ActionResult<EmployeeSignInResponse>> EmployeeLogin(EmployeeLoginRequest request, CancellationToken cancellationToken)
    {
        var outcome = await sender.Send(new OtantikPos.Inventory.Application.Hr.EmployeeSignInCommand(request.MobileNumber, request.PinCode), cancellationToken);
        if (outcome.Employee is not { } employee)
        {
            var detail = outcome.LockedUntilUtc is { } lockedUntil
                ? $"Too many wrong PINs. Try again after {clock.ToLocal(lockedUntil):HH:mm}."
                : "Wrong mobile number or PIN.";
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Sign-in failed.", detail: detail);
        }

        var (token, expiresAtUtc) = tokens.IssueEmployee(employee.Id, employee.Name, employee.PinSetAtUtc, employee.HrTabs);
        return new EmployeeSignInResponse(token, expiresAtUtc, employee.Id, employee.Name, PortalClaims.Role, HrTabSet.Split(employee.HrTabs));
    }

    // Who is signed in, and what they may do: the permissions come from the shared
    // RolePermissions table, so the Angular app shows and hides buttons by the same rules the
    // API enforces, and both stay right when a role's rights change.
    // HrTabs: the HR & Payroll tabs they hold, every one for a manager, an accountant's as a
    // manager ticked them.
    [HttpGet("me")]
    public async Task<Me> GetMe([FromServices] HrCaller hr, CancellationToken cancellationToken) =>
        Me.For(StaffClaims.IdOf(User)!, User.FindFirst(StaffClaims.Name)?.Value ?? string.Empty, StaffClaims.RoleOf(User)!.Value) with
        {
            HrTabs = HrTabSet.Split(await hr.TabsAsync(cancellationToken)),
        };
}

public sealed record SignInOption(string Id, string FullName);

public sealed record EmployeeLoginRequest(string? MobileNumber, string? PinCode);

// HrTabs: the HR & Payroll tabs a manager gave them, as the token names them.
public sealed record EmployeeSignInResponse(string Token, DateTime ExpiresAtUtc, Guid EmployeeId, string Name, string Role, IReadOnlyList<HrTabs> HrTabs);

public sealed record SignInRequest(string StaffId, string Pin);

public sealed record SignInResponse(string Token, DateTime ExpiresAtUtc, Me Staff);

public sealed record Me(string Id, string FullName, UserRole Role, IReadOnlyList<string> Permissions, IReadOnlyList<HrTabs>? HrTabs = null)
{
    public static Me For(string id, string fullName, UserRole role) =>
        new(id, fullName, role, RolePermissions.For(role).Order(StringComparer.Ordinal).ToList());
}
