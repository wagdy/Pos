using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Otantik.SharedKernel.Authorization;
using OtantikPos.Hr.Domain.Access;
using OtantikPos.Hr.Domain.Leaves;
using OtantikPos.Inventory.Application.Hr;
using OtantikPos.Node.Api.Auth;

namespace OtantikPos.Node.Api.Controllers;

// Leaves (الإجازات), on HR's Leaves & Holidays tab: the types, everyone's balances a year, and the
// requests. Seen by whoever holds the tab, who may ask for leave for someone too. Approving,
// rejecting and cancelling a request, a balance's days and the types are a manager's alone, even
// for an employee holding the tab. Employees ask for their own in the portal (PortalController).
[ApiController]
[Route("api/leaves")]
[HrTab(HrTabs.Holidays)]
[TypeFilter(typeof(HrPrivacyFilter))]
public sealed class LeavesController(ISender sender) : ControllerBase
{
    // ---- Types ----

    [HttpGet("types")]
    [HrTab(HrTabs.Holidays)]
    public Task<IReadOnlyList<LeaveTypeDto>> GetTypes(CancellationToken cancellationToken) => sender.Send(new GetLeaveTypesQuery(), cancellationToken);

    // Added under this id, or changed.
    [HttpPut("types/{typeId:guid}")]
    [HrTab(HrTabs.Holidays)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<LeaveTypeDto> SaveType(Guid typeId, LeaveTypeRequest request, CancellationToken cancellationToken) =>
        sender.Send(new SaveLeaveTypeCommand(typeId, request.Name, request.IsPaid, request.DefaultDaysPerYear, request.ProratedForNewHires), cancellationToken);

    // While nothing uses it.
    [HttpDelete("types/{typeId:guid}")]
    [HrTab(HrTabs.Holidays)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public async Task<IActionResult> DeleteType(Guid typeId, CancellationToken cancellationToken)
    {
        await sender.Send(new DeleteLeaveTypeCommand(typeId), cancellationToken);
        return NoContent();
    }

    // ---- Balances ----

    // A year's, everyone employed in it, or ?employeeId= one: a row for each paid type.
    [HttpGet("balances")]
    [HrTab(HrTabs.Holidays)]
    public Task<IReadOnlyList<LeaveBalanceDto>> GetBalances([FromQuery] int year, [FromQuery] Guid? employeeId, CancellationToken cancellationToken) =>
        sender.Send(new GetLeaveBalancesQuery(year, employeeId), cancellationToken);

    // A balance's days, in half days.
    [HttpPut("balances/{employeeId:guid}/{typeId:guid}/{year:int}")]
    [HrTab(HrTabs.Holidays)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<LeaveBalanceDto> SetBalance(Guid employeeId, Guid typeId, int year, LeaveBalanceRequest request, CancellationToken cancellationToken) =>
        sender.Send(new SetLeaveBalanceCommand(employeeId, typeId, year, request.Days, StaffName), cancellationToken);

    // ---- Requests ----

    // ?status=Pending, ?employeeId=, ?year= (this year's unless said).
    [HttpGet]
    [HrTab(HrTabs.Holidays)]
    public Task<IReadOnlyList<LeaveRequestDto>> GetRequests(
        [FromQuery] LeaveRequestStatus? status, [FromQuery] Guid? employeeId, [FromQuery] int? year, CancellationToken cancellationToken) =>
        sender.Send(new GetLeaveRequestsQuery(status, employeeId, year), cancellationToken);

    // The working days the dates would take, before asking.
    [HttpGet("days")]
    [HrTab(HrTabs.Holidays)]
    public Task<LeaveDaysDto> CountDays([FromQuery] Guid employeeId, [FromQuery] DateOnly startDate, [FromQuery] DateOnly endDate, CancellationToken cancellationToken) =>
        sender.Send(new CountLeaveDaysQuery(employeeId, startDate, endDate), cancellationToken);

    // Asked for an employee, by someone of HR.
    [HttpPost]
    [HrTab(HrTabs.Holidays, Manage = true)]
    public Task<LeaveRequestDto> Submit(SubmitLeaveRequest request, CancellationToken cancellationToken) =>
        sender.Send(new SubmitLeaveRequestCommand(
            request.RequestId, request.EmployeeId, request.LeaveTypeId, request.StartDate, request.EndDate, request.Reason, StaffName), cancellationToken);

    [HttpPost("{requestId:guid}/approve")]
    [HrTab(HrTabs.Holidays)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<LeaveRequestDto> Approve(Guid requestId, LeaveDecisionRequest? request, CancellationToken cancellationToken) =>
        sender.Send(new ApproveLeaveRequestCommand(requestId, request?.Notes, StaffId, StaffName), cancellationToken);

    [HttpPost("{requestId:guid}/reject")]
    [HrTab(HrTabs.Holidays)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<LeaveRequestDto> Reject(Guid requestId, LeaveDecisionRequest? request, CancellationToken cancellationToken) =>
        sender.Send(new RejectLeaveRequestCommand(requestId, request?.Notes, StaffId, StaffName), cancellationToken);

    // An approved one, from ?from= (the business day unless said): those days on given back.
    [HttpPost("{requestId:guid}/cancel")]
    [HrTab(HrTabs.Holidays)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<LeaveRequestDto> Cancel(Guid requestId, CancelLeaveRequest? request, CancellationToken cancellationToken) =>
        sender.Send(new CancelLeaveRequestCommand(requestId, request?.From, request?.Notes, StaffId, StaffName), cancellationToken);

    private string StaffId => StaffClaims.IdOf(User) ?? string.Empty;

    private string StaffName => User.FindFirst(StaffClaims.Name)?.Value ?? string.Empty;
}

public sealed record LeaveTypeRequest(string Name, bool IsPaid, int DefaultDaysPerYear, bool ProratedForNewHires = false);

public sealed record LeaveBalanceRequest(decimal Days);

// RequestId made by whoever asks: a retry finds the one made.
public sealed record SubmitLeaveRequest(Guid RequestId, Guid EmployeeId, Guid LeaveTypeId, DateOnly StartDate, DateOnly EndDate, string? Reason = null);

public sealed record LeaveDecisionRequest(string? Notes = null);

public sealed record CancelLeaveRequest(DateOnly? From = null, string? Notes = null);
