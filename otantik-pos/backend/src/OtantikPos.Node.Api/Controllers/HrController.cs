using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Otantik.SharedKernel.Authorization;
using OtantikPos.Hr.Domain.Attendance;
using OtantikPos.Hr.Domain.Payroll;
using OtantikPos.Inventory.Application.Approvals;
using OtantikPos.Inventory.Application.Hr;
using OtantikPos.Node.Api.Auth;

namespace OtantikPos.Node.Api.Controllers;

// HR & Payroll (الموارد البشرية والرواتب): employees, their attendance, and the monthly payroll.
// Seeing it shows salaries (HrView). Employees are entered by whoever manages HR: a manager's
// change at once (200), anyone else's asked of a manager (202) and made only when approved.
// Correcting attendance, the devices and their settings, incentives and allowances, and
// finalizing pay are a manager's. Advances and salaries are safe entries
// (POST /api/financials/vouchers, kinds EmployeeAdvance and SalaryPayment).
//
// Times of day are the restaurant's wall clock, sent without a zone: 2026-10-12T10:00:00.
[ApiController]
[Route("api/hr")]
[Authorize(Policy = Permissions.HrView)]
public sealed class HrController(ISender sender) : ControllerBase
{
    // ---- Employees ----

    [HttpGet("employees")]
    public Task<IReadOnlyList<EmployeeDto>> GetEmployees(CancellationToken cancellationToken) => sender.Send(new GetEmployeesQuery(), cancellationToken);

    // Added under the id the till made, or changed.
    [HttpPut("employees/{employeeId:guid}")]
    [Authorize(Policy = Permissions.HrManage)]
    public Task<IActionResult> SaveEmployee(Guid employeeId, SaveEmployeeRequest request, CancellationToken cancellationToken) =>
        ChangeAsync(employeeId, request.RequestId, new EmployeeRequest(new EmployeeDetails(
            request.Name, request.JobTitle, request.MonthlySalary, request.ShiftStartsAt, request.DaysOff ?? [], request.HiredOn, request.DeviceUserId)),
            request.Reason, cancellationToken);

    // Left: employed and paid to their last day.
    [HttpPost("employees/{employeeId:guid}/leave")]
    [Authorize(Policy = Permissions.HrManage)]
    public Task<IActionResult> Leave(Guid employeeId, LeaveRequest request, CancellationToken cancellationToken) =>
        ChangeAsync(employeeId, request.RequestId, new EmployeeRequest(LeavesOn: request.LastDay), request.Reason, cancellationToken);

    // Back: employed again from that day.
    [HttpPost("employees/{employeeId:guid}/return")]
    [Authorize(Policy = Permissions.HrManage)]
    public Task<IActionResult> Return(Guid employeeId, ReturnRequest request, CancellationToken cancellationToken) =>
        ChangeAsync(employeeId, request.RequestId, new EmployeeRequest(ReturnsOn: request.ReturnsOn), request.Reason, cancellationToken);

    // ---- Attendance ----

    [HttpGet("attendance")]
    public Task<AttendanceDayReport> GetAttendance([FromQuery] DateOnly date, CancellationToken cancellationToken) =>
        sender.Send(new GetAttendanceDayQuery(date), cancellationToken);

    // ?month= the month's first day.
    [HttpGet("employees/{employeeId:guid}/timesheet")]
    public Task<TimesheetDto> GetTimesheet(Guid employeeId, [FromQuery] DateOnly month, CancellationToken cancellationToken) =>
        sender.Send(new GetTimesheetQuery(employeeId, month), cancellationToken);

    [HttpPost("punches")]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<PunchDto> AddPunch(ManualPunchRequest request, CancellationToken cancellationToken) =>
        sender.Send(new AddManualPunchCommand(request.PunchId, request.EmployeeId, request.PunchedAt, request.Reason, StaffName), cancellationToken);

    [HttpPost("punches/{punchId:guid}/ignore")]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<PunchDto> IgnorePunch(Guid punchId, IgnorePunchRequest request, CancellationToken cancellationToken) =>
        sender.Send(new IgnorePunchCommand(punchId, true, request.Reason, StaffName), cancellationToken);

    [HttpPost("punches/{punchId:guid}/restore")]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<PunchDto> RestorePunch(Guid punchId, CancellationToken cancellationToken) =>
        sender.Send(new IgnorePunchCommand(punchId, false, null, StaffName), cancellationToken);

    // A day marked as leave or a day off; with no kind, the mark taken off.
    [HttpPut("employees/{employeeId:guid}/days/{date}")]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public async Task<IActionResult> MarkDay(Guid employeeId, DateOnly date, DayMarkRequest request, CancellationToken cancellationToken)
    {
        await sender.Send(new SetDayMarkCommand(employeeId, date, request.Kind, request.Notes, StaffName), cancellationToken);
        return NoContent();
    }

    // The days suspended for a manager: a punch-in left 18 hours without its punch-out. From the
    // first of last month to today unless asked otherwise.
    [HttpGet("attendance/suspended")]
    public Task<IReadOnlyList<EmployeeDayDto>> GetSuspended([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        return sender.Send(new GetSuspendedDaysQuery(from ?? new DateOnly(today.Year, today.Month, 1).AddMonths(-1), to ?? today), cancellationToken);
    }

    // A suspended day resolved by the manager's punch-out: the day worked out again from it.
    [HttpPost("attendance/resolve")]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<AttendanceDayDto> Resolve(ResolveDayRequest request, CancellationToken cancellationToken) =>
        sender.Send(new ResolveSuspendedDayCommand(
            request.PunchId, request.EmployeeId, request.Date, request.PunchedOutAt, request.Reason, StaffName), cancellationToken);

    // A manager's administrative penalty on a day, in hours, with the reason; with no hours, taken off.
    [HttpPut("employees/{employeeId:guid}/days/{date}/penalty")]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public async Task<IActionResult> SetPenalty(Guid employeeId, DateOnly date, PenaltyRequest request, CancellationToken cancellationToken)
    {
        await sender.Send(new SetAdministrativePenaltyCommand(employeeId, date, request.Hours, request.Reason, StaffName), cancellationToken);
        return NoContent();
    }

    // A month's administrative penalties, everyone's; ?month= its first day.
    [HttpGet("penalties")]
    public Task<IReadOnlyList<AdministrativePenaltyDto>> GetPenalties([FromQuery] DateOnly month, CancellationToken cancellationToken) =>
        sender.Send(new GetAdministrativePenaltiesQuery(month), cancellationToken);

    // A year's public holidays, by their first day.
    [HttpGet("holidays")]
    public Task<IReadOnlyList<PublicHolidayDto>> GetHolidays([FromQuery] int year, CancellationToken cancellationToken) =>
        sender.Send(new GetPublicHolidaysQuery(year), cancellationToken);

    // A public holiday added under this id, or changed: a manager's.
    [HttpPut("holidays/{holidayId:guid}")]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<PublicHolidayDto> SaveHoliday(Guid holidayId, HolidayRequest request, CancellationToken cancellationToken) =>
        sender.Send(new SavePublicHolidayCommand(holidayId, request.Name, request.FirstDay, request.LastDay, StaffName), cancellationToken);

    [HttpDelete("holidays/{holidayId:guid}")]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public async Task<IActionResult> DeleteHoliday(Guid holidayId, CancellationToken cancellationToken)
    {
        await sender.Send(new DeletePublicHolidayCommand(holidayId), cancellationToken);
        return NoContent();
    }

    [HttpGet("settings")]
    public Task<AttendancePolicyDto> GetSettings(CancellationToken cancellationToken) => sender.Send(new GetAttendanceSettingsQuery(), cancellationToken);

    [HttpPut("settings")]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<AttendancePolicyDto> SaveSettings(AttendancePolicyDto request, CancellationToken cancellationToken) =>
        sender.Send(new SaveAttendanceSettingsCommand(request.GraceMinutes, request.DoubleScanMinutes), cancellationToken);

    [HttpGet("devices")]
    public Task<IReadOnlyList<AttendanceDeviceDto>> GetDevices(CancellationToken cancellationToken) =>
        sender.Send(new GetAttendanceDevicesQuery(), cancellationToken);

    // Approved by name, or renamed.
    [HttpPost("devices/{deviceId:guid}/approve")]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<AttendanceDeviceDto> ApproveDevice(Guid deviceId, ApproveDeviceRequest request, CancellationToken cancellationToken) =>
        sender.Send(new ApproveAttendanceDeviceCommand(deviceId, request.Name ?? string.Empty, StaffName), cancellationToken);

    // Refused again: nothing more it sends is taken.
    [HttpPost("devices/{deviceId:guid}/revoke")]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<AttendanceDeviceDto> RevokeDevice(Guid deviceId, CancellationToken cancellationToken) =>
        sender.Send(new ApproveAttendanceDeviceCommand(deviceId, null, StaffName), cancellationToken);

    // ---- Payroll ----

    // Each slip as it stands: a draft worked out now, a finalized one as it was finalized.
    [HttpGet("payroll")]
    public Task<PayrollMonthDto> GetPayroll([FromQuery] DateOnly month, CancellationToken cancellationToken) =>
        sender.Send(new GetPayrollQuery(month), cancellationToken);

    // The month's drafts saved as they stand.
    [HttpPost("payroll/calculate")]
    [Authorize(Policy = Permissions.HrManage)]
    public Task<PayrollMonthDto> CalculatePayroll([FromQuery] DateOnly month, CancellationToken cancellationToken) =>
        sender.Send(new CalculatePayrollCommand(month), cancellationToken);

    [HttpPost("payroll/{employeeId:guid}/finalize")]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<PayrollSlipDto> FinalizeSlip(Guid employeeId, [FromQuery] DateOnly month, CancellationToken cancellationToken) =>
        sender.Send(new FinalizePayrollSlipCommand(month, employeeId, StaffName), cancellationToken);

    [HttpPost("payroll/adjustments")]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<PayrollAdjustmentDto> AddAdjustment(AdjustmentRequest request, CancellationToken cancellationToken) =>
        sender.Send(new AddPayrollAdjustmentCommand(
            request.AdjustmentId, request.EmployeeId, request.Month, request.Kind, request.Amount, request.Reason, request.ForDay, StaffName), cancellationToken);

    [HttpPost("payroll/adjustments/{adjustmentId:guid}/void")]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<PayrollAdjustmentDto> VoidAdjustment(Guid adjustmentId, CancellationToken cancellationToken) =>
        sender.Send(new VoidPayrollAdjustmentCommand(adjustmentId, StaffName), cancellationToken);

    private async Task<IActionResult> ChangeAsync(Guid employeeId, Guid requestId, EmployeeRequest change, string? reason, CancellationToken cancellationToken)
    {
        var result = await sender.Send(
            new ChangeEmployeeCommand(employeeId, requestId, change, reason, StaffClaims.IdOf(User) ?? string.Empty, StaffName, CanApprove), cancellationToken);
        return result.Outcome == ApprovalOutcome.Requested
            ? Accepted(new ApprovalResultDto(result.Outcome, result.Request!))
            : Ok(result.Employee);
    }

    private string StaffName => User.FindFirst(StaffClaims.Name)?.Value ?? string.Empty;

    private bool CanApprove => StaffClaims.RoleOf(User) is { } role && RolePermissions.Has(role, Permissions.ApprovalDecide);
}

public sealed record SaveEmployeeRequest(
    Guid RequestId,
    string Name,
    string JobTitle,
    decimal MonthlySalary,
    TimeOnly ShiftStartsAt,
    IReadOnlyList<DayOfWeek>? DaysOff,
    DateOnly HiredOn,
    string? DeviceUserId = null,
    string? Reason = null);

public sealed record LeaveRequest(Guid RequestId, DateOnly LastDay, string? Reason = null);

public sealed record ReturnRequest(Guid RequestId, DateOnly ReturnsOn, string? Reason = null);

public sealed record ManualPunchRequest(Guid PunchId, Guid EmployeeId, DateTime PunchedAt, string Reason);

public sealed record IgnorePunchRequest(string Reason);

public sealed record DayMarkRequest(DayMarkKind? Kind, string? Notes = null);

public sealed record ResolveDayRequest(Guid PunchId, Guid EmployeeId, DateOnly Date, DateTime PunchedOutAt, string Reason);

public sealed record PenaltyRequest(decimal? Hours, string? Reason = null);

public sealed record HolidayRequest(string Name, DateOnly FirstDay, DateOnly LastDay);

public sealed record ApproveDeviceRequest(string? Name);

public sealed record AdjustmentRequest(
    Guid AdjustmentId, Guid EmployeeId, DateOnly Month, AdjustmentKind Kind, decimal Amount, string? Reason = null, DateOnly? ForDay = null);
