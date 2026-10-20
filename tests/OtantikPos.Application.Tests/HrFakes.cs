using Otantik.BuildingBlocks;
using OtantikPos.Hr.Domain;
using OtantikPos.Hr.Domain.Attendance;
using OtantikPos.Hr.Domain.Employees;
using OtantikPos.Hr.Domain.Payroll;
using OtantikPos.Inventory.Application.Hr;

namespace OtantikPos.Application.Tests;

// HR's records in memory, as the till's database keeps them.
public sealed class HrStore
{
    public EmployeeStore Employees { get; } = new();
    public DeviceStore Devices { get; } = new();
    public PunchStore Punches { get; } = new();
    public DayMarkStore Marks { get; } = new();
    public PenaltyStore Penalties { get; } = new();
    public DailyRecordStore Records { get; } = new();
    public AdjustmentStore Adjustments { get; } = new();
    public SlipStore Slips { get; } = new();
    public AttendanceSettingsStore Settings { get; } = new();
}

public abstract class ListStore<T> : IRepository<T> where T : AggregateRoot
{
    protected List<T> Items { get; } = [];

    public IReadOnlyList<T> All => Items;

    public Task<T?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Items.SingleOrDefault(i => i.Id == id));

    public void Add(T aggregate) => Items.Add(aggregate);
}

public sealed class EmployeeStore : ListStore<Employee>, IEmployeeRepository
{
    public Task<IReadOnlyList<Employee>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Employee>>(Items.OrderBy(e => e.Name).ToList());

    public Task<Employee?> GetByDeviceUserIdAsync(string deviceUserId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Items.SingleOrDefault(e => e.DeviceUserId == deviceUserId));
}

public sealed class DeviceStore : ListStore<AttendanceDevice>, IAttendanceDeviceRepository
{
    public Task<AttendanceDevice?> GetBySerialNumberAsync(string serialNumber, CancellationToken cancellationToken = default) =>
        Task.FromResult(Items.SingleOrDefault(d => d.SerialNumber == serialNumber));

    public Task<IReadOnlyList<AttendanceDevice>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AttendanceDevice>>(Items.ToList());
}

public sealed class PunchStore : ListStore<AttendanceLog>, IAttendanceLogRepository
{
    public Task<IReadOnlyList<AttendanceLog>> GetFromDeviceAsync(string serialNumber, DateTime from, DateTime to, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AttendanceLog>>(Items.Where(l => l.DeviceSerialNumber == serialNumber && l.PunchedAt >= from && l.PunchedAt <= to).ToList());

    public Task<IReadOnlyList<AttendanceLog>> GetBetweenAsync(DateTime from, DateTime to, Guid? employeeId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AttendanceLog>>(Items
            .Where(l => l.PunchedAt >= from && l.PunchedAt < to && (employeeId == null || l.EmployeeId == employeeId))
            .OrderBy(l => l.PunchedAt).ToList());

    public Task<IReadOnlyList<AttendanceLog>> GetUnassignedAsync(string deviceUserId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AttendanceLog>>(Items.Where(l => l.EmployeeId == null && l.DeviceUserId == deviceUserId).ToList());
}

public sealed class DayMarkStore : ListStore<DayMark>, IDayMarkRepository
{
    public Task<DayMark?> GetAsync(Guid employeeId, DateOnly date, CancellationToken cancellationToken = default) =>
        Task.FromResult(Items.SingleOrDefault(m => m.EmployeeId == employeeId && m.Date == date));

    public Task<IReadOnlyList<DayMark>> GetBetweenAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DayMark>>(Items.Where(m => m.Date >= from && m.Date <= to).ToList());

    public void Remove(DayMark mark) => Items.Remove(mark);
}

public sealed class PenaltyStore : ListStore<AdministrativePenalty>, IAdministrativePenaltyRepository
{
    public Task<AdministrativePenalty?> GetAsync(Guid employeeId, DateOnly date, CancellationToken cancellationToken = default) =>
        Task.FromResult(Items.SingleOrDefault(p => p.EmployeeId == employeeId && p.Date == date));

    public Task<IReadOnlyList<AdministrativePenalty>> GetBetweenAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AdministrativePenalty>>(Items.Where(p => p.Date >= from && p.Date <= to).ToList());

    public void Remove(AdministrativePenalty penalty) => Items.Remove(penalty);
}

public sealed class DailyRecordStore : ListStore<DailyRecord>, IDailyRecordRepository
{
    public Task<IReadOnlyList<DailyRecord>> GetForEmployeeAsync(Guid employeeId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DailyRecord>>(Items.Where(r => r.EmployeeId == employeeId && r.Date >= from && r.Date <= to).ToList());
}

public sealed class AdjustmentStore : ListStore<PayrollAdjustment>, IPayrollAdjustmentRepository
{
    public Task<IReadOnlyList<PayrollAdjustment>> GetForMonthAsync(DateOnly month, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PayrollAdjustment>>(Items.Where(a => a.Month == month).ToList());
}

public sealed class SlipStore : ListStore<PayrollSlip>, IPayrollSlipRepository
{
    public Task<PayrollSlip?> GetAsync(Guid employeeId, DateOnly month, CancellationToken cancellationToken = default) =>
        Task.FromResult(Items.SingleOrDefault(s => s.EmployeeId == employeeId && s.Month == month));

    public Task<IReadOnlyList<PayrollSlip>> GetForMonthAsync(DateOnly month, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PayrollSlip>>(Items.Where(s => s.Month == month).ToList());
}

public sealed class AttendanceSettingsStore : IAttendanceSettings
{
    public AttendancePolicy Policy { get; set; } = AttendancePolicy.Default;

    public Task<AttendancePolicy> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Policy);

    public Task SaveAsync(AttendancePolicy policy, CancellationToken cancellationToken)
    {
        Policy = policy;
        return Task.CompletedTask;
    }
}
