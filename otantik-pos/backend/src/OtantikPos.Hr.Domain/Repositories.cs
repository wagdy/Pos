using Otantik.BuildingBlocks;
using OtantikPos.Hr.Domain.Access;
using OtantikPos.Hr.Domain.Attendance;
using OtantikPos.Hr.Domain.Custody;
using OtantikPos.Hr.Domain.Employees;
using OtantikPos.Hr.Domain.Leaves;
using OtantikPos.Hr.Domain.Performance;
using OtantikPos.Hr.Domain.Payroll;

namespace OtantikPos.Hr.Domain;

// What HR keeps, as the use cases ask for it. Read lists are not tracked; what is changed is.

public interface IEmployeeRepository : IRepository<Employee>
{
    // Everyone, left or not, by name.
    Task<IReadOnlyList<Employee>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<Employee?> GetByDeviceUserIdAsync(string deviceUserId, CancellationToken cancellationToken = default);

    Task<Employee?> GetByNationalIdAsync(string nationalId, CancellationToken cancellationToken = default);

    // Tracked: the portal signs in by it.
    Task<Employee?> GetByMobileNumberAsync(string mobileNumber, CancellationToken cancellationToken = default);

    // Tracked: everyone in a department, left or not.
    Task<IReadOnlyList<Employee>> GetInDepartmentAsync(Guid departmentId, CancellationToken cancellationToken = default);

    // Tracked: everyone holding a role, left or not.
    Task<IReadOnlyList<Employee>> GetWithRoleAsync(Guid jobRoleId, CancellationToken cancellationToken = default);
}

// The photo or scan of an employee's ID card, kept apart from them so a list of employees never
// loads a megabyte each. Written in the same unit of work as the employee.
public interface IEmployeeIdPhotoStore
{
    void Put(Guid employeeId, byte[] content);

    Task RemoveAsync(Guid employeeId, CancellationToken cancellationToken = default);

    Task<byte[]?> GetAsync(Guid employeeId, CancellationToken cancellationToken = default);
}

public interface IAssetCategoryRepository : IRepository<AssetCategory>
{
    // By name.
    Task<IReadOnlyList<AssetCategory>> GetAllAsync(CancellationToken cancellationToken = default);

    void Remove(AssetCategory category);
}

public interface IAssetItemRepository : IRepository<AssetItem>
{
    // By name; of one origin, or all of them.
    Task<IReadOnlyList<AssetItem>> GetAllAsync(AssetOrigin? origin = null, CancellationToken cancellationToken = default);

    // What is in an employee's custody now.
    Task<IReadOnlyList<AssetItem>> GetInCustodyOfAsync(Guid employeeId, CancellationToken cancellationToken = default);

    Task<bool> AnyInCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default);

    // The asset a posted invoice line made, if it made one.
    Task<AssetItem?> GetBySourceLineAsync(Guid invoiceLineId, CancellationToken cancellationToken = default);

    // Tracked: the assets an invoice's lines made.
    Task<IReadOnlyList<AssetItem>> GetFromInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken = default);

    // Only one that never moved: its invoice voided before it was handed out.
    void Remove(AssetItem asset);
}

public interface IAssetTransactionRepository : IRepository<AssetTransaction>
{
    // An asset's vouchers, the latest first.
    Task<IReadOnlyList<AssetTransaction>> GetForAssetAsync(Guid assetItemId, CancellationToken cancellationToken = default);

    // The vouchers of the business days from `from` to `to`, the latest first.
    Task<IReadOnlyList<AssetTransaction>> GetBetweenAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    Task<bool> AnyForAssetAsync(Guid assetItemId, CancellationToken cancellationToken = default);
}

public interface IJobRoleRepository : IRepository<JobRole>
{
    // Every department's, by name.
    Task<IReadOnlyList<JobRole>> GetAllAsync(CancellationToken cancellationToken = default);

    // Tracked: a department's.
    Task<IReadOnlyList<JobRole>> GetInDepartmentAsync(Guid departmentId, CancellationToken cancellationToken = default);

    void Remove(JobRole role);
}

public interface IDepartmentRepository : IRepository<Department>
{
    // By name.
    Task<IReadOnlyList<Department>> GetAllAsync(CancellationToken cancellationToken = default);

    void Remove(Department department);
}

public interface IAttendanceDeviceRepository : IRepository<AttendanceDevice>
{
    Task<AttendanceDevice?> GetBySerialNumberAsync(string serialNumber, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AttendanceDevice>> GetAllAsync(CancellationToken cancellationToken = default);
}

public interface IAttendanceLogRepository : IRepository<AttendanceLog>
{
    // A device's punches already kept from `from` to `to`, both included: a resent one is kept once.
    Task<IReadOnlyList<AttendanceLog>> GetFromDeviceAsync(string serialNumber, DateTime from, DateTime to, CancellationToken cancellationToken = default);

    // The punch it sent last (the latest on its clock of those it sent last), ignored ones left out.
    Task<AttendanceLog?> GetLastReceivedFromDeviceAsync(string serialNumber, CancellationToken cancellationToken = default);

    // Every punch from `from` up to `to`, ignored ones and ones nobody has yet too; one
    // employee's alone when given.
    Task<IReadOnlyList<AttendanceLog>> GetBetweenAsync(DateTime from, DateTime to, Guid? employeeId = null, CancellationToken cancellationToken = default);

    // Tracked: the punches under a user number nobody had, for the employee who has it now.
    Task<IReadOnlyList<AttendanceLog>> GetUnassignedAsync(string deviceUserId, CancellationToken cancellationToken = default);

    // How many punches wait under each user number nobody has.
    Task<IReadOnlyDictionary<string, int>> CountUnassignedAsync(CancellationToken cancellationToken = default);
}

public interface IDeviceCommandRepository : IRepository<DeviceCommand>
{
    // Tracked: the oldest of a device's commands not handed to it yet.
    Task<DeviceCommand?> GetNextAsync(string serialNumber, CancellationToken cancellationToken = default);

    // Tracked.
    Task<DeviceCommand?> GetByNumberAsync(string serialNumber, int number, CancellationToken cancellationToken = default);

    // A device's highest command number so far; 0 before its first.
    Task<int> GetLastNumberAsync(string serialNumber, CancellationToken cancellationToken = default);

    // Each device's newest few, newest first.
    Task<IReadOnlyList<DeviceCommand>> GetRecentAsync(int perDevice, CancellationToken cancellationToken = default);
}

public interface IDeviceUserRepository : IRepository<DeviceUser>
{
    // Tracked: the users a device has told the till of.
    Task<IReadOnlyList<DeviceUser>> GetForDeviceAsync(string serialNumber, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeviceUser>> GetAllAsync(CancellationToken cancellationToken = default);
}

public interface IDayMarkRepository : IRepository<DayMark>
{
    Task<DayMark?> GetAsync(Guid employeeId, DateOnly date, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DayMark>> GetBetweenAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    // An approved leave request's days, to change.
    Task<IReadOnlyList<DayMark>> GetForLeaveRequestAsync(Guid leaveRequestId, CancellationToken cancellationToken = default);

    void Remove(DayMark mark);
}

public interface IAdministrativePenaltyRepository : IRepository<AdministrativePenalty>
{
    // A manager's own penalty on the day: not a disciplinary action's.
    Task<AdministrativePenalty?> GetAsync(Guid employeeId, DateOnly date, CancellationToken cancellationToken = default);

    // A disciplinary action's penalty deduction.
    Task<AdministrativePenalty?> GetForDisciplinaryActionAsync(Guid disciplinaryActionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AdministrativePenalty>> GetBetweenAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    void Remove(AdministrativePenalty penalty);
}

public interface IPublicHolidayRepository : IRepository<PublicHoliday>
{
    // The holidays with a day from `from` to `to`, both included, by their first day.
    Task<IReadOnlyList<PublicHoliday>> GetBetweenAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    void Remove(PublicHoliday holiday);
}

public interface IDailyRecordRepository : IRepository<DailyRecord>
{
    // Tracked: worked out again when a slip is.
    Task<IReadOnlyList<DailyRecord>> GetForEmployeeAsync(Guid employeeId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}

public interface IPayrollAdjustmentRepository : IRepository<PayrollAdjustment>
{
    Task<IReadOnlyList<PayrollAdjustment>> GetForMonthAsync(DateOnly month, CancellationToken cancellationToken = default);
}

// Shares of advances handed out with no one named, taken-back ones too.
public interface IAdvanceAllocationRepository : IRepository<AdvanceAllocation>
{
    // The shares of these advances: safe entries or HR advances, by their ids.
    Task<IReadOnlyList<AdvanceAllocation>> GetForAdvancesAsync(IReadOnlyCollection<Guid> sourceIds, CancellationToken cancellationToken = default);

    // The shares deducted in a payroll month: everyone's, or one employee's.
    Task<IReadOnlyList<AdvanceAllocation>> GetForPayrollMonthAsync(DateOnly month, Guid? employeeId = null, CancellationToken cancellationToken = default);
}

// Advances paid outside the till, recorded in HR only; voided ones too.
public interface IHrAdvanceRepository : IRepository<HrAdvance>
{
    // Those paid to an employee and deducted in a payroll month: everyone's, or one employee's.
    Task<IReadOnlyList<HrAdvance>> GetForPayrollMonthAsync(DateOnly month, Guid? employeeId = null, CancellationToken cancellationToken = default);

    // Those paid to no one yet, not voided, given out or not.
    Task<IReadOnlyList<HrAdvance>> GetPendingAsync(CancellationToken cancellationToken = default);
}

public interface IPayrollSlipRepository : IRepository<PayrollSlip>
{
    Task<PayrollSlip?> GetAsync(Guid employeeId, DateOnly month, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PayrollSlip>> GetForMonthAsync(DateOnly month, CancellationToken cancellationToken = default);
}

// The HR tabs of till accounts that are not a manager's, by the account's id.
public interface IStaffHrAccessRepository : IRepository<StaffHrAccess>
{
    Task<StaffHrAccess?> GetByStaffIdAsync(string staffId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StaffHrAccess>> GetAllAsync(CancellationToken cancellationToken = default);
}

// The kinds of leave, by name.
public interface ILeaveTypeRepository : IRepository<LeaveType>
{
    Task<IReadOnlyList<LeaveType>> GetAllAsync(CancellationToken cancellationToken = default);

    void Remove(LeaveType type);
}

// The leave balances: one to an employee, a type and a year.
public interface IEmployeeLeaveBalanceRepository : IRepository<EmployeeLeaveBalance>
{
    Task<EmployeeLeaveBalance?> GetAsync(Guid employeeId, Guid leaveTypeId, int year, CancellationToken cancellationToken = default);

    // A year's, everyone's or one employee's.
    Task<IReadOnlyList<EmployeeLeaveBalance>> GetForYearAsync(int year, Guid? employeeId = null, CancellationToken cancellationToken = default);

    Task<bool> AnyForTypeAsync(Guid leaveTypeId, CancellationToken cancellationToken = default);
}

public interface ILeaveRequestRepository : IRepository<LeaveRequest>
{
    // Waiting for a manager, the oldest first.
    Task<IReadOnlyList<LeaveRequest>> GetPendingAsync(CancellationToken cancellationToken = default);

    // One employee's, the latest first.
    Task<IReadOnlyList<LeaveRequest>> GetForEmployeeAsync(Guid employeeId, CancellationToken cancellationToken = default);

    // Everyone's starting in a year, the latest first.
    Task<IReadOnlyList<LeaveRequest>> GetForYearAsync(int year, CancellationToken cancellationToken = default);

    // An employee's waiting or approved ones whose dates meet these: a day is asked for once.
    Task<IReadOnlyList<LeaveRequest>> GetOverlappingAsync(Guid employeeId, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default);

    Task<bool> AnyForTypeAsync(Guid leaveTypeId, CancellationToken cancellationToken = default);
}

// The KPIs, by name, retired ones too.
public interface IPerformanceKpiRepository : IRepository<PerformanceKpi>
{
    Task<IReadOnlyList<PerformanceKpi>> GetAllAsync(CancellationToken cancellationToken = default);

    void Remove(PerformanceKpi kpi);
}

public interface IPerformanceEvaluationRepository : IRepository<PerformanceEvaluation>
{
    // One employee's, the latest first.
    Task<IReadOnlyList<PerformanceEvaluation>> GetForEmployeeAsync(Guid employeeId, CancellationToken cancellationToken = default);

    Task<bool> AnyForKpiAsync(Guid kpiId, CancellationToken cancellationToken = default);
}

public interface IDisciplinaryActionRepository : IRepository<DisciplinaryAction>
{
    // One employee's, the latest first: their record, and what the next escalates from.
    Task<IReadOnlyList<DisciplinaryAction>> GetForEmployeeAsync(Guid employeeId, CancellationToken cancellationToken = default);
}
