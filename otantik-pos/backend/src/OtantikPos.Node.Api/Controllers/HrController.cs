using System.Globalization;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Otantik.SharedKernel.Authorization;
using OtantikPos.Hr.Domain.Attendance;
using OtantikPos.Hr.Domain.Employees;
using OtantikPos.Hr.Domain.Payroll;
using OtantikPos.Inventory.Application.Approvals;
using OtantikPos.Inventory.Application.Hr;
using OtantikPos.Node.Api.Auth;
using OtantikPos.Node.Infrastructure.Excel;
using OtantikPos.Ordering.Application.Ports;

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

    // The departments, by name, with how many are in each.
    [HttpGet("departments")]
    public Task<IReadOnlyList<DepartmentDto>> GetDepartments(CancellationToken cancellationToken) => sender.Send(new GetDepartmentsQuery(), cancellationToken);

    // Added under this id, or renamed: a manager's.
    [HttpPut("departments/{departmentId:guid}")]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<DepartmentDto> SaveDepartment(Guid departmentId, DepartmentRequest request, CancellationToken cancellationToken) =>
        sender.Send(new SaveDepartmentCommand(departmentId, request.Name), cancellationToken);

    // A role added to a department under this id, or renamed for everyone holding it: a manager's.
    [HttpPut("departments/{departmentId:guid}/roles/{roleId:guid}")]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<JobRoleDto> SaveRole(Guid departmentId, Guid roleId, DepartmentRequest request, CancellationToken cancellationToken) =>
        sender.Send(new SaveJobRoleCommand(roleId, departmentId, request.Name), cancellationToken);

    // Deleted, whoever holds it taking ?moveTo= first, another of the department's roles: a manager's.
    [HttpDelete("roles/{roleId:guid}")]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public async Task<IActionResult> DeleteRole(Guid roleId, [FromQuery] Guid? moveTo, CancellationToken cancellationToken)
    {
        await sender.Send(new DeleteJobRoleCommand(roleId, moveTo), cancellationToken);
        return NoContent();
    }

    // Deleted, its employees moved first to ?moveTo= when it has any: a manager's.
    [HttpDelete("departments/{departmentId:guid}")]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public async Task<IActionResult> DeleteDepartment(Guid departmentId, [FromQuery] Guid? moveTo, CancellationToken cancellationToken)
    {
        await sender.Send(new DeleteDepartmentCommand(departmentId, moveTo), cancellationToken);
        return NoContent();
    }

    // Added under the id the till made, or changed.
    [HttpPut("employees/{employeeId:guid}")]
    [Authorize(Policy = Permissions.HrManage)]
    public Task<IActionResult> SaveEmployee(Guid employeeId, SaveEmployeeRequest request, CancellationToken cancellationToken) =>
        ChangeAsync(employeeId, request.RequestId, new EmployeeRequest(new EmployeeDetails(
            request.Name, request.JobTitle, request.MonthlySalary, request.ShiftStartsAt, request.DaysOff ?? [], request.HiredOn, request.DeviceUserId,
            request.EmploymentType ?? EmploymentType.Monthly, request.DailyWage ?? 0, request.DepartmentId, request.JobRoleId)),
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

    // ---- Excel ----

    // The spreadsheet to bring employees in from: the columns, with the departments and roles.
    [HttpGet("employees/template")]
    [Authorize(Policy = Permissions.HrManage)]
    public async Task<IActionResult> GetEmployeesTemplate(CancellationToken cancellationToken)
    {
        var departments = await sender.Send(new GetDepartmentsQuery(), cancellationToken);
        return File(EmployeeWorkbook.Template(departments), EmployeeWorkbook.ContentType, EmployeeWorkbook.TemplateFileName);
    }

    // A filled-in template: each good row an employee, added for a manager and asked of one for
    // anyone else; the rest said why not.
    [HttpPost("employees/import")]
    [Authorize(Policy = Permissions.HrManage)]
    [RequestSizeLimit(MaxImportBytes + 64 * 1024)]
    public async Task<EmployeeImportResult> ImportEmployees(IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length > MaxImportBytes)
            throw new Otantik.BuildingBlocks.DomainException("The file is larger than 5 MB: a sheet of employees is far smaller. Is it the right file?");
        await using var stream = file.OpenReadStream();
        return await sender.Send(
            new ImportEmployeesCommand(EmployeeWorkbook.Read(stream), StaffClaims.IdOf(User) ?? string.Empty, StaffName, CanApprove), cancellationToken);
    }

    // Everyone employed now, in the template's columns: to file, or to add to and import again.
    [HttpGet("employees/export")]
    public async Task<IActionResult> ExportEmployees([FromServices] IBusinessCalendar calendar, CancellationToken cancellationToken)
    {
        var employees = (await sender.Send(new GetEmployeesQuery(), cancellationToken)).Where(e => e.IsActive).ToList();
        var departments = await sender.Send(new GetDepartmentsQuery(), cancellationToken);
        var today = calendar.BusinessDateOf(DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return File(EmployeeWorkbook.Export(employees, departments), EmployeeWorkbook.ContentType, $"employees-{today}.xlsx");
    }

    // The month's payroll as the Payroll tab shows it, with its totals.
    [HttpGet("payroll/export")]
    public async Task<IActionResult> ExportPayroll([FromQuery] DateOnly month, [FromServices] IBusinessCalendar calendar, CancellationToken cancellationToken)
    {
        var payroll = await sender.Send(new GetPayrollQuery(month), cancellationToken);
        var departments = (await sender.Send(new GetDepartmentsQuery(), cancellationToken)).ToDictionary(d => d.Id, d => d.Name);
        return File(PayrollWorkbook.Export(payroll, departments, calendar.BusinessDateOf(DateTime.UtcNow)), PayrollWorkbook.ContentType, PayrollWorkbook.FileName(month));
    }

    private const int MaxImportBytes = 5 * 1024 * 1024;

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

    // Who is in now: each employee's state on their own day, from the punches as they come in.
    [HttpGet("attendance/now")]
    public Task<PresenceReport> GetPresence(CancellationToken cancellationToken) => sender.Send(new GetPresenceQuery(), cancellationToken);

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

    // A device's punches that came after their month's pay was finalized, not seen to yet.
    [HttpGet("attendance/late-punches")]
    public Task<IReadOnlyList<LatePunchDto>> GetLatePunches(CancellationToken cancellationToken) => sender.Send(new GetLatePunchesQuery(), cancellationToken);

    [HttpPost("attendance/late-punches/settle")]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public async Task<object> SettleLatePunches(SettleRequest request, CancellationToken cancellationToken) =>
        new { Settled = await sender.Send(new SettleLatePunchesCommand(request.PunchIds ?? [], StaffName), cancellationToken) };

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

    // Where a device reaches this till: the device's own port when one is set, and this
    // machine's addresses on its networks. No port: the one the screens are served on.
    [HttpGet("devices/connection")]
    public object GetDeviceConnection([FromServices] IConfiguration configuration) =>
        new { DevicePort = Hosting.DevicePort.Of(configuration), Addresses = Hosting.DevicePort.Addresses() };

    // The device reached by the till over its own port (ZKTeco's, 4370), beside ADMS: whether it
    // answers, and what it is. A manager's, as the till reaches out for it.
    [HttpPost("devices/test")]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<DeviceProbe> TestDevice(DeviceAddressRequest request, CancellationToken cancellationToken) =>
        sender.Send(new TestDeviceConnectionQuery(request.Address, request.Port ?? 4370, request.CommKey ?? 0), cancellationToken);

    // Its users and attendance log read now and kept; the device approved when it was not.
    [HttpPost("devices/sync")]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<DeviceSyncResult> SyncDevice(DeviceAddressRequest request, CancellationToken cancellationToken) =>
        sender.Send(new SyncDeviceCommand(request.Address, request.Port ?? 4370, request.CommKey ?? 0, StaffName), cancellationToken);

    // A command left for a device: its users read, or its attendance from a day read again. The
    // device takes it the next time it asks, within seconds.
    [HttpPost("devices/{deviceId:guid}/read-users")]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<DeviceCommandDto> ReadDeviceUsers(Guid deviceId, CancellationToken cancellationToken) =>
        sender.Send(new QueueDeviceCommandCommand(deviceId, DeviceCommandKind.ReadUsers, null, null, StaffName), cancellationToken);

    [HttpPost("devices/{deviceId:guid}/read-attendance")]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<DeviceCommandDto> ReadDeviceAttendance(Guid deviceId, ReadAttendanceRequest request, CancellationToken cancellationToken) =>
        sender.Send(new QueueDeviceCommandCommand(deviceId, DeviceCommandKind.ReadAttendance, request.From, request.To, StaffName), cancellationToken);

    // The users the devices have listed, and who on the till has each number.
    [HttpGet("device-users")]
    public Task<IReadOnlyList<DeviceUserDto>> GetDeviceUsers(CancellationToken cancellationToken) => sender.Send(new GetDeviceUsersQuery(), cancellationToken);

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
    // None for flexible hours.
    TimeOnly? ShiftStartsAt,
    IReadOnlyList<DayOfWeek>? DaysOff,
    DateOnly HiredOn,
    string? DeviceUserId = null,
    string? Reason = null,
    // Monthly unless said; a daily one's wage; General unless given.
    EmploymentType? EmploymentType = null,
    decimal? DailyWage = null,
    Guid? DepartmentId = null,
    // One of their department's roles; the job title alone makes one when the department has none of that name.
    Guid? JobRoleId = null);

public sealed record DepartmentRequest(string Name);

public sealed record LeaveRequest(Guid RequestId, DateOnly LastDay, string? Reason = null);

public sealed record ReturnRequest(Guid RequestId, DateOnly ReturnsOn, string? Reason = null);

public sealed record ManualPunchRequest(Guid PunchId, Guid EmployeeId, DateTime PunchedAt, string Reason);

public sealed record IgnorePunchRequest(string Reason);

public sealed record DayMarkRequest(DayMarkKind? Kind, string? Notes = null);

public sealed record ResolveDayRequest(Guid PunchId, Guid EmployeeId, DateOnly Date, DateTime PunchedOutAt, string Reason);

public sealed record PenaltyRequest(decimal? Hours, string? Reason = null);

public sealed record HolidayRequest(string Name, DateOnly FirstDay, DateOnly LastDay);

public sealed record SettleRequest(IReadOnlyList<Guid>? PunchIds);

public sealed record ReadAttendanceRequest(DateOnly? From, DateOnly? To = null);

// The device's IP address; its port, 4370 unless said; its comm key, 0 (none) unless said.
public sealed record DeviceAddressRequest(string Address, int? Port = null, int? CommKey = null);

public sealed record ApproveDeviceRequest(string? Name);

public sealed record AdjustmentRequest(
    Guid AdjustmentId, Guid EmployeeId, DateOnly Month, AdjustmentKind Kind, decimal Amount, string? Reason = null, DateOnly? ForDay = null);
