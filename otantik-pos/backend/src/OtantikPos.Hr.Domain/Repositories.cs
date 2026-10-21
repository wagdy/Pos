using Otantik.BuildingBlocks;
using OtantikPos.Hr.Domain.Attendance;
using OtantikPos.Hr.Domain.Employees;
using OtantikPos.Hr.Domain.Payroll;

namespace OtantikPos.Hr.Domain;

// What HR keeps, as the use cases ask for it. Read lists are not tracked; what is changed is.

public interface IEmployeeRepository : IRepository<Employee>
{
    // Everyone, left or not, by name.
    Task<IReadOnlyList<Employee>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<Employee?> GetByDeviceUserIdAsync(string deviceUserId, CancellationToken cancellationToken = default);

    // Tracked: everyone in a department, left or not.
    Task<IReadOnlyList<Employee>> GetInDepartmentAsync(Guid departmentId, CancellationToken cancellationToken = default);

    // Tracked: everyone holding a role, left or not.
    Task<IReadOnlyList<Employee>> GetWithRoleAsync(Guid jobRoleId, CancellationToken cancellationToken = default);
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

    void Remove(DayMark mark);
}

public interface IAdministrativePenaltyRepository : IRepository<AdministrativePenalty>
{
    Task<AdministrativePenalty?> GetAsync(Guid employeeId, DateOnly date, CancellationToken cancellationToken = default);

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

public interface IPayrollSlipRepository : IRepository<PayrollSlip>
{
    Task<PayrollSlip?> GetAsync(Guid employeeId, DateOnly month, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PayrollSlip>> GetForMonthAsync(DateOnly month, CancellationToken cancellationToken = default);
}
