using System.Globalization;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Otantik.SharedKernel.Authorization;
using OtantikPos.Hr.Domain.Access;
using OtantikPos.Hr.Domain.Attendance;
using OtantikPos.Hr.Domain.Employees;
using OtantikPos.Hr.Domain.Payroll;
using OtantikPos.Inventory.Application.Approvals;
using OtantikPos.Inventory.Application.Hr;
using OtantikPos.Node.Api.Auth;
using OtantikPos.Node.Infrastructure.Excel;
using OtantikPos.Node.Infrastructure.Identity;
using OtantikPos.Ordering.Application.Ports;

namespace OtantikPos.Node.Api.Controllers;

// HR & Payroll (الموارد البشرية والرواتب): employees, their attendance, and the monthly payroll.
// Each endpoint is its tabs' (HrTab): a manager holds them all; an accountant, and an employee in
// the portal, the ones a manager gave them (صلاحيات الموظفين). What anyone earns, and their
// national ID and ID card, reach only whoever holds the Employees or Payroll tab (HrPrivacyFilter).
// Employees are entered by whoever manages HR: a manager's change at once (200), anyone else's
// asked of a manager (202) and made only when approved.
// Correcting attendance, the devices and their settings, incentives and allowances, and
// finalizing pay are a manager's. Advances and salaries are safe entries
// (POST /api/financials/vouchers, kinds EmployeeAdvance and SalaryPayment).
//
// Times of day are the restaurant's wall clock, sent without a zone: 2026-10-12T10:00:00.
[ApiController]
[Route("api/hr")]
[HrTab(HrTabs.All)]
[TypeFilter(typeof(HrPrivacyFilter))]
public sealed class HrController(ISender sender) : ControllerBase
{
    // The till's business day, for whoever works a tab: a day's attendance, a month's pay.
    [HttpGet("today")]
    [HrTab(HrTabs.All)]
    public object GetToday([FromServices] IBusinessCalendar calendar) => new { Today = calendar.BusinessDateOf(DateTime.UtcNow) };

    // ---- Employee permissions (صلاحيات الموظفين): a manager's ----

    // Everyone employed, and the accountants' accounts, with the tabs each has.
    [HttpGet("access")]
    [HrTab(HrTabs.All)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public async Task<HrAccessDto> GetAccess([FromServices] StaffDirectory staff, CancellationToken cancellationToken) =>
        await sender.Send(new GetHrAccessQuery(await GivenAsync(staff, cancellationToken)), cancellationToken);

    // An employee's tabs, ["Now", "Attendance"]: they work them in the portal from their next sign-in.
    [HttpPut("access/employees/{employeeId:guid}")]
    [HrTab(HrTabs.All)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<EmployeeHrAccessDto> SetEmployeeAccess(Guid employeeId, HrTabsRequest request, CancellationToken cancellationToken) =>
        sender.Send(new SetEmployeeHrTabsCommand(employeeId, HrTabSet.Join(request.Tabs)), cancellationToken);

    // An accountant's tabs: only those from now on.
    [HttpPut("access/staff/{staffId}")]
    [HrTab(HrTabs.All)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public async Task<ActionResult<StaffHrAccessDto>> SetStaffAccess(
        string staffId, HrTabsRequest request, [FromServices] StaffDirectory staff, CancellationToken cancellationToken)
    {
        if ((await GivenAsync(staff, cancellationToken)).FirstOrDefault(s => s.Id == staffId) is not { } account)
            return NotFound();
        return await sender.Send(new SetStaffHrTabsCommand(account, HrTabSet.Join(request.Tabs), StaffName), cancellationToken);
    }

    // The till accounts given tabs: those who see HR and are not a manager, the accountants.
    private static async Task<IReadOnlyList<HrStaffAccount>> GivenAsync(StaffDirectory staff, CancellationToken cancellationToken) =>
        (await staff.GetTillStaffAsync(cancellationToken))
            .Where(s => RolePermissions.Has(s.Role, Permissions.HrView) && !RolePermissions.Has(s.Role, Permissions.ApprovalDecide))
            .Select(s => new HrStaffAccount(s.Id, s.FullName, s.Role.ToString()))
            .ToList();

    // ---- Employees ----

    [HttpGet("employees")]
    [HrTab(HrTabs.Employees | HrTabs.Attendance | HrTabs.Corrections | HrTabs.Payroll | HrTabs.Advances)]
    public Task<IReadOnlyList<EmployeeDto>> GetEmployees(CancellationToken cancellationToken) => sender.Send(new GetEmployeesQuery(), cancellationToken);

    // One employee, left or not: their profile.
    [HttpGet("employees/{employeeId:guid}")]
    [HrTab(HrTabs.Employees)]
    public async Task<ActionResult<EmployeeDto>> GetEmployee(Guid employeeId, CancellationToken cancellationToken) =>
        await sender.Send(new GetEmployeeQuery(employeeId), cancellationToken) is { } employee ? employee : NotFound();

    // Their ID card, photographed or scanned (صورة البطاقة): a JPEG, PNG, WebP or PDF of up to
    // 5 MB, replacing the one there was. Whoever manages HR, at once: it is not money.
    [HttpPut("employees/{employeeId:guid}/id-photo")]
    [HrTab(HrTabs.Employees, Manage = true)]
    [RequestSizeLimit(Employee.MaxIdPhotoBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = Employee.MaxIdPhotoBytes + 64 * 1024)]
    public async Task<ActionResult<EmployeeDto>> UploadIdPhoto(Guid employeeId, IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length > Employee.MaxIdPhotoBytes)
            return UnprocessableEntity(new { errors = new[] { "The file must be under 5 MB." } });
        using var content = new MemoryStream();
        await file.CopyToAsync(content, cancellationToken);
        return await sender.Send(new UploadEmployeeIdPhotoCommand(employeeId, file.FileName, file.ContentType, content.ToArray(), StaffName), cancellationToken);
    }

    // Seen by whoever sees HR; ?download=true to save it under its own name.
    [HttpGet("employees/{employeeId:guid}/id-photo")]
    [HrTab(HrTabs.Employees)]
    public async Task<IActionResult> GetIdPhoto(Guid employeeId, [FromQuery] bool download, CancellationToken cancellationToken)
    {
        if (await sender.Send(new GetEmployeeIdPhotoQuery(employeeId), cancellationToken) is not { } photo)
            return NotFound();
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.CacheControl = "private, no-store";
        return download ? File(photo.Content, photo.ContentType, photo.FileName) : File(photo.Content, photo.ContentType);
    }

    [HttpDelete("employees/{employeeId:guid}/id-photo")]
    [HrTab(HrTabs.Employees, Manage = true)]
    public Task<EmployeeDto> RemoveIdPhoto(Guid employeeId, CancellationToken cancellationToken) =>
        sender.Send(new RemoveEmployeeIdPhotoCommand(employeeId), cancellationToken);

    // What is in their custody now (العهدة), and since when.
    [HttpGet("employees/{employeeId:guid}/custody")]
    [HrTab(HrTabs.Employees)]
    public Task<IReadOnlyList<OtantikPos.Inventory.Application.Custody.CustodyItemDto>> GetCustody(Guid employeeId, CancellationToken cancellationToken) =>
        sender.Send(new OtantikPos.Inventory.Application.Custody.GetEmployeeCustodyQuery(employeeId), cancellationToken);

    // Their portal PIN, a manager's to set: 4 to 6 digits, kept hashed, never shown. Setting one
    // signs them out of the portal and unlocks it; they sign in with their mobile number.
    [HttpPut("employees/{employeeId:guid}/pin")]
    [HrTab(HrTabs.Employees)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<EmployeeDto> SetPin(Guid employeeId, PinRequest request, CancellationToken cancellationToken) =>
        sender.Send(new SetEmployeePinCommand(employeeId, request.Pin ?? string.Empty), cancellationToken);

    // No PIN: they cannot sign in to the portal.
    [HttpDelete("employees/{employeeId:guid}/pin")]
    [HrTab(HrTabs.Employees)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<EmployeeDto> RemovePin(Guid employeeId, CancellationToken cancellationToken) =>
        sender.Send(new SetEmployeePinCommand(employeeId, null), cancellationToken);

    // The departments, by name, with how many are in each.
    [HttpGet("departments")]
    [HrTab(HrTabs.All)]
    public Task<IReadOnlyList<DepartmentDto>> GetDepartments(CancellationToken cancellationToken) => sender.Send(new GetDepartmentsQuery(), cancellationToken);

    // Added under this id, or renamed: a manager's.
    [HttpPut("departments/{departmentId:guid}")]
    [HrTab(HrTabs.Departments)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<DepartmentDto> SaveDepartment(Guid departmentId, DepartmentRequest request, CancellationToken cancellationToken) =>
        sender.Send(new SaveDepartmentCommand(departmentId, request.Name), cancellationToken);

    // A role added to a department under this id, or renamed for everyone holding it: a manager's.
    [HttpPut("departments/{departmentId:guid}/roles/{roleId:guid}")]
    [HrTab(HrTabs.Departments)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<JobRoleDto> SaveRole(Guid departmentId, Guid roleId, DepartmentRequest request, CancellationToken cancellationToken) =>
        sender.Send(new SaveJobRoleCommand(roleId, departmentId, request.Name), cancellationToken);

    // Deleted, whoever holds it taking ?moveTo= first, another of the department's roles: a manager's.
    [HttpDelete("roles/{roleId:guid}")]
    [HrTab(HrTabs.Departments)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public async Task<IActionResult> DeleteRole(Guid roleId, [FromQuery] Guid? moveTo, CancellationToken cancellationToken)
    {
        await sender.Send(new DeleteJobRoleCommand(roleId, moveTo), cancellationToken);
        return NoContent();
    }

    // Deleted, its employees moved first to ?moveTo= when it has any: a manager's.
    [HttpDelete("departments/{departmentId:guid}")]
    [HrTab(HrTabs.Departments)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public async Task<IActionResult> DeleteDepartment(Guid departmentId, [FromQuery] Guid? moveTo, CancellationToken cancellationToken)
    {
        await sender.Send(new DeleteDepartmentCommand(departmentId, moveTo), cancellationToken);
        return NoContent();
    }

    // Added under the id the till made, or changed.
    [HttpPut("employees/{employeeId:guid}")]
    [HrTab(HrTabs.Employees, Manage = true)]
    public Task<IActionResult> SaveEmployee(Guid employeeId, SaveEmployeeRequest request, CancellationToken cancellationToken) =>
        ChangeAsync(employeeId, request.RequestId, new EmployeeRequest(new EmployeeDetails(
            request.Name, request.JobTitle, request.MonthlySalary, request.ShiftStartsAt, request.DaysOff ?? [], request.HiredOn, request.DeviceUserId,
            request.EmploymentType ?? EmploymentType.Monthly, request.DailyWage ?? 0, request.DepartmentId, request.JobRoleId, request.NationalId,
            request.MobileNumber)),
            request.Reason, cancellationToken);

    // Left: employed and paid to their last day.
    [HttpPost("employees/{employeeId:guid}/leave")]
    [HrTab(HrTabs.Employees, Manage = true)]
    public Task<IActionResult> Leave(Guid employeeId, LeaveRequest request, CancellationToken cancellationToken) =>
        ChangeAsync(employeeId, request.RequestId, new EmployeeRequest(LeavesOn: request.LastDay), request.Reason, cancellationToken);

    // Back: employed again from that day.
    [HttpPost("employees/{employeeId:guid}/return")]
    [HrTab(HrTabs.Employees, Manage = true)]
    public Task<IActionResult> Return(Guid employeeId, ReturnRequest request, CancellationToken cancellationToken) =>
        ChangeAsync(employeeId, request.RequestId, new EmployeeRequest(ReturnsOn: request.ReturnsOn), request.Reason, cancellationToken);

    // ---- Excel ----

    // The spreadsheet to bring employees in from: the columns, with the departments and roles.
    [HttpGet("employees/template")]
    [HrTab(HrTabs.Employees, Manage = true)]
    public async Task<IActionResult> GetEmployeesTemplate(CancellationToken cancellationToken)
    {
        var departments = await sender.Send(new GetDepartmentsQuery(), cancellationToken);
        return File(EmployeeWorkbook.Template(departments), EmployeeWorkbook.ContentType, EmployeeWorkbook.TemplateFileName);
    }

    // A filled-in template: each good row an employee, added for a manager and asked of one for
    // anyone else; the rest said why not.
    [HttpPost("employees/import")]
    [HrTab(HrTabs.Employees, Manage = true)]
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
    [HrTab(HrTabs.Employees)]
    public async Task<IActionResult> ExportEmployees([FromServices] IBusinessCalendar calendar, CancellationToken cancellationToken)
    {
        var employees = (await sender.Send(new GetEmployeesQuery(), cancellationToken)).Where(e => e.IsActive).ToList();
        var departments = await sender.Send(new GetDepartmentsQuery(), cancellationToken);
        var today = calendar.BusinessDateOf(DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return File(EmployeeWorkbook.Export(employees, departments), EmployeeWorkbook.ContentType, $"employees-{today}.xlsx");
    }

    // The month's payroll as the Payroll tab shows it, with its totals.
    [HttpGet("payroll/export")]
    [HrTab(HrTabs.Payroll)]
    public async Task<IActionResult> ExportPayroll([FromQuery] DateOnly month, [FromServices] IBusinessCalendar calendar, CancellationToken cancellationToken)
    {
        var payroll = await sender.Send(new GetPayrollQuery(month), cancellationToken);
        var departments = (await sender.Send(new GetDepartmentsQuery(), cancellationToken)).ToDictionary(d => d.Id, d => d.Name);
        return File(PayrollWorkbook.Export(payroll, departments, calendar.BusinessDateOf(DateTime.UtcNow)), PayrollWorkbook.ContentType, PayrollWorkbook.FileName(month));
    }

    private const int MaxImportBytes = 5 * 1024 * 1024;

    // ---- Attendance ----

    [HttpGet("attendance")]
    [HrTab(HrTabs.Attendance)]
    public Task<AttendanceDayReport> GetAttendance([FromQuery] DateOnly date, CancellationToken cancellationToken) =>
        sender.Send(new GetAttendanceDayQuery(date), cancellationToken);

    // ?month= the month's first day.
    [HttpGet("employees/{employeeId:guid}/timesheet")]
    [HrTab(HrTabs.Attendance)]
    public Task<TimesheetDto> GetTimesheet(Guid employeeId, [FromQuery] DateOnly month, CancellationToken cancellationToken) =>
        sender.Send(new GetTimesheetQuery(employeeId, month), cancellationToken);

    [HttpPost("punches")]
    [HrTab(HrTabs.Attendance | HrTabs.Corrections)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<PunchDto> AddPunch(ManualPunchRequest request, CancellationToken cancellationToken) =>
        sender.Send(new AddManualPunchCommand(request.PunchId, request.EmployeeId, request.PunchedAt, request.Reason, StaffName), cancellationToken);

    [HttpPost("punches/{punchId:guid}/ignore")]
    [HrTab(HrTabs.Attendance | HrTabs.Corrections)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<PunchDto> IgnorePunch(Guid punchId, IgnorePunchRequest request, CancellationToken cancellationToken) =>
        sender.Send(new IgnorePunchCommand(punchId, true, request.Reason, StaffName), cancellationToken);

    [HttpPost("punches/{punchId:guid}/restore")]
    [HrTab(HrTabs.Attendance | HrTabs.Corrections)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<PunchDto> RestorePunch(Guid punchId, CancellationToken cancellationToken) =>
        sender.Send(new IgnorePunchCommand(punchId, false, null, StaffName), cancellationToken);

    // A day marked as leave or a day off; with no kind, the mark taken off.
    [HttpPut("employees/{employeeId:guid}/days/{date}")]
    [HrTab(HrTabs.Attendance | HrTabs.Corrections)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public async Task<IActionResult> MarkDay(Guid employeeId, DateOnly date, DayMarkRequest request, CancellationToken cancellationToken)
    {
        await sender.Send(new SetDayMarkCommand(employeeId, date, request.Kind, request.Notes, StaffName), cancellationToken);
        return NoContent();
    }

    // Who is in now: each employee's state on their own day, from the punches as they come in.
    [HttpGet("attendance/now")]
    [HrTab(HrTabs.Now)]
    public Task<PresenceReport> GetPresence(CancellationToken cancellationToken) => sender.Send(new GetPresenceQuery(), cancellationToken);

    // The days suspended for a manager: a punch-in left 18 hours without its punch-out. From the
    // first of last month to today unless asked otherwise.
    [HttpGet("attendance/suspended")]
    [HrTab(HrTabs.Corrections)]
    public Task<IReadOnlyList<EmployeeDayDto>> GetSuspended([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        return sender.Send(new GetSuspendedDaysQuery(from ?? new DateOnly(today.Year, today.Month, 1).AddMonths(-1), to ?? today), cancellationToken);
    }

    // A suspended day resolved by the manager's punch-out: the day worked out again from it.
    [HttpPost("attendance/resolve")]
    [HrTab(HrTabs.Attendance | HrTabs.Corrections)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<AttendanceDayDto> Resolve(ResolveDayRequest request, CancellationToken cancellationToken) =>
        sender.Send(new ResolveSuspendedDayCommand(
            request.PunchId, request.EmployeeId, request.Date, request.PunchedOutAt, request.Reason, StaffName), cancellationToken);

    // A device's punches that came after their month's pay was finalized, not seen to yet.
    [HttpGet("attendance/late-punches")]
    [HrTab(HrTabs.Corrections)]
    public Task<IReadOnlyList<LatePunchDto>> GetLatePunches(CancellationToken cancellationToken) => sender.Send(new GetLatePunchesQuery(), cancellationToken);

    [HttpPost("attendance/late-punches/settle")]
    [HrTab(HrTabs.Corrections)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public async Task<object> SettleLatePunches(SettleRequest request, CancellationToken cancellationToken) =>
        new { Settled = await sender.Send(new SettleLatePunchesCommand(request.PunchIds ?? [], StaffName), cancellationToken) };

    // A manager's administrative penalty on a day, in hours, with the reason; with no hours, taken off.
    [HttpPut("employees/{employeeId:guid}/days/{date}/penalty")]
    [HrTab(HrTabs.Attendance | HrTabs.Corrections)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public async Task<IActionResult> SetPenalty(Guid employeeId, DateOnly date, PenaltyRequest request, CancellationToken cancellationToken)
    {
        await sender.Send(new SetAdministrativePenaltyCommand(employeeId, date, request.Hours, request.Reason, StaffName), cancellationToken);
        return NoContent();
    }

    // A month's administrative penalties, everyone's; ?month= its first day.
    [HttpGet("penalties")]
    [HrTab(HrTabs.Corrections)]
    public Task<IReadOnlyList<AdministrativePenaltyDto>> GetPenalties([FromQuery] DateOnly month, CancellationToken cancellationToken) =>
        sender.Send(new GetAdministrativePenaltiesQuery(month), cancellationToken);

    // A year's public holidays, by their first day.
    [HttpGet("holidays")]
    [HrTab(HrTabs.Holidays)]
    public Task<IReadOnlyList<PublicHolidayDto>> GetHolidays([FromQuery] int year, CancellationToken cancellationToken) =>
        sender.Send(new GetPublicHolidaysQuery(year), cancellationToken);

    // A public holiday added under this id, or changed: a manager's.
    [HttpPut("holidays/{holidayId:guid}")]
    [HrTab(HrTabs.Holidays)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<PublicHolidayDto> SaveHoliday(Guid holidayId, HolidayRequest request, CancellationToken cancellationToken) =>
        sender.Send(new SavePublicHolidayCommand(holidayId, request.Name, request.FirstDay, request.LastDay, StaffName), cancellationToken);

    [HttpDelete("holidays/{holidayId:guid}")]
    [HrTab(HrTabs.Holidays)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public async Task<IActionResult> DeleteHoliday(Guid holidayId, CancellationToken cancellationToken)
    {
        await sender.Send(new DeletePublicHolidayCommand(holidayId), cancellationToken);
        return NoContent();
    }

    // Where a device reaches this till: the device's own port when one is set, and this
    // machine's addresses on its networks. No port: the one the screens are served on. The
    // employee portal's port too, when there is one: where phones on the Wi-Fi open it.
    [HttpGet("devices/connection")]
    [HrTab(HrTabs.Now | HrTabs.Employees | HrTabs.Devices)]
    public object GetDeviceConnection([FromServices] IConfiguration configuration) =>
        new { DevicePort = Hosting.DevicePort.Of(configuration), PortalPort = Hosting.PortalPort.Of(configuration), Addresses = Hosting.DevicePort.Addresses() };

    // The device reached by the till over its own port (ZKTeco's, 4370), beside ADMS: whether it
    // answers, and what it is. A manager's, as the till reaches out for it.
    [HttpPost("devices/test")]
    [HrTab(HrTabs.Employees | HrTabs.Devices)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<DeviceProbe> TestDevice(DeviceAddressRequest request, CancellationToken cancellationToken) =>
        sender.Send(new TestDeviceConnectionQuery(request.Address, request.Port ?? 4370, request.CommKey ?? 0), cancellationToken);

    // Its users and attendance log read now and kept; the device approved when it was not.
    [HttpPost("devices/sync")]
    [HrTab(HrTabs.Employees | HrTabs.Devices)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<DeviceSyncResult> SyncDevice(DeviceAddressRequest request, CancellationToken cancellationToken) =>
        sender.Send(new SyncDeviceCommand(request.Address, request.Port ?? 4370, request.CommKey ?? 0, StaffName), cancellationToken);

    // A command left for a device: its users read, or its attendance from a day read again. The
    // device takes it the next time it asks, within seconds.
    [HttpPost("devices/{deviceId:guid}/read-users")]
    [HrTab(HrTabs.Employees | HrTabs.Devices)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<DeviceCommandDto> ReadDeviceUsers(Guid deviceId, CancellationToken cancellationToken) =>
        sender.Send(new QueueDeviceCommandCommand(deviceId, DeviceCommandKind.ReadUsers, null, null, StaffName), cancellationToken);

    [HttpPost("devices/{deviceId:guid}/read-attendance")]
    [HrTab(HrTabs.Devices)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<DeviceCommandDto> ReadDeviceAttendance(Guid deviceId, ReadAttendanceRequest request, CancellationToken cancellationToken) =>
        sender.Send(new QueueDeviceCommandCommand(deviceId, DeviceCommandKind.ReadAttendance, request.From, request.To, StaffName), cancellationToken);

    // The users the devices have listed, and who on the till has each number.
    [HttpGet("device-users")]
    [HrTab(HrTabs.Employees | HrTabs.Devices)]
    public Task<IReadOnlyList<DeviceUserDto>> GetDeviceUsers(CancellationToken cancellationToken) => sender.Send(new GetDeviceUsersQuery(), cancellationToken);

    [HttpGet("settings")]
    [HrTab(HrTabs.Devices)]
    public Task<AttendancePolicyDto> GetSettings(CancellationToken cancellationToken) => sender.Send(new GetAttendanceSettingsQuery(), cancellationToken);

    [HttpPut("settings")]
    [HrTab(HrTabs.Devices)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<AttendancePolicyDto> SaveSettings(AttendancePolicyDto request, CancellationToken cancellationToken) =>
        sender.Send(new SaveAttendanceSettingsCommand(request.GraceMinutes, request.DoubleScanMinutes), cancellationToken);

    [HttpGet("devices")]
    [HrTab(HrTabs.Now | HrTabs.Employees | HrTabs.Devices)]
    public Task<IReadOnlyList<AttendanceDeviceDto>> GetDevices(CancellationToken cancellationToken) =>
        sender.Send(new GetAttendanceDevicesQuery(), cancellationToken);

    // Approved by name, or renamed.
    [HttpPost("devices/{deviceId:guid}/approve")]
    [HrTab(HrTabs.Devices)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<AttendanceDeviceDto> ApproveDevice(Guid deviceId, ApproveDeviceRequest request, CancellationToken cancellationToken) =>
        sender.Send(new ApproveAttendanceDeviceCommand(deviceId, request.Name ?? string.Empty, StaffName), cancellationToken);

    // Refused again: nothing more it sends is taken.
    [HttpPost("devices/{deviceId:guid}/revoke")]
    [HrTab(HrTabs.Devices)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<AttendanceDeviceDto> RevokeDevice(Guid deviceId, CancellationToken cancellationToken) =>
        sender.Send(new ApproveAttendanceDeviceCommand(deviceId, null, StaffName), cancellationToken);

    // ---- Advances ----

    // A payroll month's advances, each deducted from whose pay; and the advances handed out with
    // no one named (السلفيات المعلقة), every one not all given out.
    [HttpGet("advances")]
    [HrTab(HrTabs.Advances)]
    public Task<AdvancesDto> GetAdvances([FromQuery] DateOnly month, CancellationToken cancellationToken) =>
        sender.Send(new GetAdvancesQuery(month), cancellationToken);

    // Advances handed out with no one named, given out: a share each, in part or the rest of them.
    [HttpPost("advances/{advanceId:guid}/distribute")]
    [HrTab(HrTabs.Advances)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<PendingAdvanceDto> DistributeAdvance(Guid advanceId, DistributeAdvanceRequest request, CancellationToken cancellationToken) =>
        sender.Send(new DistributeAdvanceCommand(advanceId, request.Shares ?? [], StaffName), cancellationToken);

    // An advance paid outside the till (from the owner's own pocket, say), recorded in HR only: no
    // cash leaves the safe or the takings. To an employee, or to no one yet (a lump sum). A
    // manager's at once (200); anyone else's asked of a manager (202).
    [HttpPost("advances")]
    [HrTab(HrTabs.Advances, Manage = true)]
    public async Task<IActionResult> RecordHrAdvance(RecordHrAdvanceRequest request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new RecordHrAdvanceCommand(
            request.AdvanceId, request.RequestId ?? Guid.NewGuid(), new HrAdvanceInput(request.Amount, request.Date, request.EmployeeId, request.Notes),
            StaffClaims.IdOf(User) ?? string.Empty, StaffName, CanApprove), cancellationToken);
        return result.Outcome == ApprovalOutcome.Requested ? Accepted(new ApprovalResultDto(result.Outcome, result.Request!)) : Ok(result.Advance);
    }

    [HttpPost("advances/{advanceId:guid}/void")]
    [HrTab(HrTabs.Advances)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<HrAdvanceDto> VoidHrAdvance(Guid advanceId, CancellationToken cancellationToken) =>
        sender.Send(new VoidHrAdvanceCommand(advanceId, StaffName), cancellationToken);

    // A share taken back, for its advances to be given out again.
    [HttpPost("advance-shares/{shareId:guid}/take-back")]
    [HrTab(HrTabs.Advances)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<PendingAdvanceDto> TakeBackAdvanceShare(Guid shareId, CancellationToken cancellationToken) =>
        sender.Send(new TakeBackAdvanceShareCommand(shareId, StaffName), cancellationToken);

    // ---- Payroll ----

    // Each slip as it stands: a draft worked out now, a finalized one as it was finalized.
    [HttpGet("payroll")]
    [HrTab(HrTabs.Payroll)]
    public Task<PayrollMonthDto> GetPayroll([FromQuery] DateOnly month, CancellationToken cancellationToken) =>
        sender.Send(new GetPayrollQuery(month), cancellationToken);

    // A day's labour cost (تكلفة يوم): who punched in, each at their day's rate, by department, the
    // day's overtime pay beside it.
    [HttpGet("payroll/daily-cost")]
    [HrTab(HrTabs.Payroll)]
    public Task<DailyLabourCostDto> GetDailyCost([FromQuery] DateOnly date, CancellationToken cancellationToken) =>
        sender.Send(new GetDailyLabourCostQuery(date), cancellationToken);

    // The month's drafts saved as they stand.
    [HttpPost("payroll/calculate")]
    [HrTab(HrTabs.Payroll, Manage = true)]
    public Task<PayrollMonthDto> CalculatePayroll([FromQuery] DateOnly month, CancellationToken cancellationToken) =>
        sender.Send(new CalculatePayrollCommand(month), cancellationToken);

    [HttpPost("payroll/{employeeId:guid}/finalize")]
    [HrTab(HrTabs.Payroll)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<PayrollSlipDto> FinalizeSlip(Guid employeeId, [FromQuery] DateOnly month, CancellationToken cancellationToken) =>
        sender.Send(new FinalizePayrollSlipCommand(month, employeeId, StaffName), cancellationToken);

    [HttpPost("payroll/adjustments")]
    [HrTab(HrTabs.Payroll)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<PayrollAdjustmentDto> AddAdjustment(AdjustmentRequest request, CancellationToken cancellationToken) =>
        sender.Send(new AddPayrollAdjustmentCommand(
            request.AdjustmentId, request.EmployeeId, request.Month, request.Kind, request.Amount, request.Reason, request.ForDay, StaffName), cancellationToken);

    [HttpPost("payroll/adjustments/{adjustmentId:guid}/void")]
    [HrTab(HrTabs.Payroll)]
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
    Guid? JobRoleId = null,
    // رقم البطاقة and رقم الموبايل; none when left out.
    string? NationalId = null,
    string? MobileNumber = null);

public sealed record DepartmentRequest(string Name);

// The tabs given, by name: ["Now", "Attendance"]; none for none.
public sealed record HrTabsRequest(IReadOnlyList<HrTabs>? Tabs);

public sealed record PinRequest(string? Pin);

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

public sealed record DistributeAdvanceRequest(IReadOnlyList<AdvanceShareRequest>? Shares);

// AdvanceId and RequestId made by the till: a retry finds what was made, or asked.
public sealed record RecordHrAdvanceRequest(Guid AdvanceId, decimal Amount, DateOnly Date, Guid? EmployeeId = null, string? Notes = null, Guid? RequestId = null);

// The device's IP address; its port, 4370 unless said; its comm key, 0 (none) unless said.
public sealed record DeviceAddressRequest(string Address, int? Port = null, int? CommKey = null);

public sealed record ApproveDeviceRequest(string? Name);

public sealed record AdjustmentRequest(
    Guid AdjustmentId, Guid EmployeeId, DateOnly Month, AdjustmentKind Kind, decimal Amount, string? Reason = null, DateOnly? ForDay = null);
