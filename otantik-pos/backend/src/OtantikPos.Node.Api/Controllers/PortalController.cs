using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OtantikPos.Inventory.Application.Hr;
using OtantikPos.Node.Api.Auth;

namespace OtantikPos.Node.Api.Controllers;

// The employee self-service portal (البوابة): read-only, and only ever the signed-in employee's
// own record, taken from their token, never from the request. Their details and ID card, their
// payslips (the month's draft as it stands, or as finalized), and what is in their custody.
[ApiController]
[Route("api/portal")]
[Authorize(Policy = PortalClaims.Policy)]
public sealed class PortalController(ISender sender) : ControllerBase
{
    private Guid Me => PortalClaims.EmployeeIdOf(User) ?? throw new UnauthorizedAccessException();

    [HttpGet("me")]
    public async Task<ActionResult<PortalProfileDto>> GetMe(CancellationToken cancellationToken) =>
        await sender.Send(new GetPortalProfileQuery(Me), cancellationToken) is { } profile ? profile : NotFound();

    [HttpGet("id-photo")]
    public async Task<IActionResult> GetIdPhoto(CancellationToken cancellationToken)
    {
        if (await sender.Send(new GetEmployeeIdPhotoQuery(Me), cancellationToken) is not { } photo)
            return NotFound();
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.CacheControl = "private, no-store";
        return File(photo.Content, photo.ContentType);
    }

    // The months there is a payslip for, the latest first.
    [HttpGet("payslip-months")]
    public Task<IReadOnlyList<DateOnly>> GetPayslipMonths(CancellationToken cancellationToken) =>
        sender.Send(new GetPortalMonthsQuery(Me), cancellationToken);

    // ?month= its first day; the business month's when left out.
    [HttpGet("payslip")]
    public async Task<ActionResult<PayrollSlipDto>> GetPayslip([FromQuery] DateOnly? month, CancellationToken cancellationToken)
    {
        var months = await sender.Send(new GetPortalMonthsQuery(Me), cancellationToken);
        var asked = month ?? (months.Count > 0 ? months[0] : null);
        if (asked is null)
            return NotFound();
        return await sender.Send(new GetPortalPayslipQuery(Me, asked.Value), cancellationToken) is { } slip ? slip : NotFound();
    }

    // Name, category and how many, never what it cost: the profile's custody, with its costs, is
    // the managers' (HrController).
    // Their leave this year: each paid type's balance, and their requests.
    [HttpGet("leaves")]
    public Task<EmployeeLeavesDto> GetLeaves(CancellationToken cancellationToken) =>
        sender.Send(new GetEmployeeLeavesQuery(Me), cancellationToken);

    // The working days the dates would take, before asking.
    [HttpGet("leaves/days")]
    public Task<LeaveDaysDto> CountLeaveDays([FromQuery] DateOnly startDate, [FromQuery] DateOnly endDate, CancellationToken cancellationToken) =>
        sender.Send(new CountLeaveDaysQuery(Me, startDate, endDate), cancellationToken);

    // Asked for themselves: it waits for a manager.
    [HttpPost("leaves")]
    public Task<LeaveRequestDto> RequestLeave(PortalLeaveRequest request, CancellationToken cancellationToken) =>
        sender.Send(new SubmitLeaveRequestCommand(
            request.RequestId, Me, request.LeaveTypeId, request.StartDate, request.EndDate, request.Reason,
            User.FindFirst(StaffClaims.Name)?.Value ?? string.Empty), cancellationToken);

    // Their evaluations and disciplinary actions, read-only: what was withdrawn left out.
    [HttpGet("performance")]
    public Task<EmployeePerformanceDto> GetPerformance(CancellationToken cancellationToken) =>
        sender.Send(new GetEmployeePerformanceQuery(Me, ForEmployee: true), cancellationToken);

    [HttpGet("custody")]
    public Task<IReadOnlyList<EmployeeCustodyDto>> GetCustody(CancellationToken cancellationToken) =>
        sender.Send(new GetPortalCustodyQuery(Me), cancellationToken);
}

// RequestId made by the phone: a retry finds the one made.
public sealed record PortalLeaveRequest(Guid RequestId, Guid LeaveTypeId, DateOnly StartDate, DateOnly EndDate, string? Reason = null);
