using Microsoft.EntityFrameworkCore;
using OtantikPos.Hr.Domain;
using OtantikPos.Hr.Domain.Attendance;
using OtantikPos.Hr.Domain.Employees;
using OtantikPos.Hr.Domain.Payroll;
using OtantikPos.Inventory.Application.Hr;
using OtantikPos.Node.Infrastructure.Persistence;

namespace OtantikPos.Node.Infrastructure.Hr;

internal sealed class EmployeeRepository(NodeDbContext db) : IEmployeeRepository
{
    public Task<Employee?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.Employees.SingleOrDefaultAsync(e => e.Id == id, cancellationToken);

    public void Add(Employee aggregate) => db.Employees.Add(aggregate);

    public async Task<IReadOnlyList<Employee>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await db.Employees.AsNoTracking().OrderBy(e => e.Name).ToListAsync(cancellationToken);

    public Task<Employee?> GetByDeviceUserIdAsync(string deviceUserId, CancellationToken cancellationToken = default) =>
        db.Employees.AsNoTracking().SingleOrDefaultAsync(e => e.DeviceUserId == deviceUserId, cancellationToken);

    public Task<Employee?> GetByNationalIdAsync(string nationalId, CancellationToken cancellationToken = default) =>
        db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.NationalId == nationalId, cancellationToken);

    public async Task<IReadOnlyList<Employee>> GetInDepartmentAsync(Guid departmentId, CancellationToken cancellationToken = default) =>
        await db.Employees.Where(e => e.DepartmentId == departmentId).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Employee>> GetWithRoleAsync(Guid jobRoleId, CancellationToken cancellationToken = default) =>
        await db.Employees.Where(e => e.JobRoleId == jobRoleId).ToListAsync(cancellationToken);
}

// An employee's ID card photo: one row per employee, its bytes alone.
public sealed class EmployeeIdPhoto
{
    public Guid EmployeeId { get; init; }
    public byte[] Content { get; set; } = [];
}

internal sealed class EmployeeIdPhotoStore(NodeDbContext db) : IEmployeeIdPhotoStore
{
    // Replaces the one there was.
    public void Put(Guid employeeId, byte[] content)
    {
        var existing = db.EmployeeIdPhotos.Local.FirstOrDefault(p => p.EmployeeId == employeeId)
            ?? db.EmployeeIdPhotos.Find(employeeId);
        if (existing is null)
            db.EmployeeIdPhotos.Add(new EmployeeIdPhoto { EmployeeId = employeeId, Content = content });
        else
            existing.Content = content;
    }

    public async Task RemoveAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        if (await db.EmployeeIdPhotos.FindAsync([employeeId], cancellationToken) is { } photo)
            db.EmployeeIdPhotos.Remove(photo);
    }

    public async Task<byte[]?> GetAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
        await db.EmployeeIdPhotos.AsNoTracking()
            .Where(p => p.EmployeeId == employeeId)
            .Select(p => p.Content)
            .SingleOrDefaultAsync(cancellationToken);
}

internal sealed class JobRoleRepository(NodeDbContext db) : IJobRoleRepository
{
    public Task<JobRole?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.JobRoles.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

    public void Add(JobRole aggregate) => db.JobRoles.Add(aggregate);

    public void Remove(JobRole role) => db.JobRoles.Remove(role);

    public async Task<IReadOnlyList<JobRole>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await db.JobRoles.AsNoTracking().OrderBy(r => r.Name).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<JobRole>> GetInDepartmentAsync(Guid departmentId, CancellationToken cancellationToken = default) =>
        await db.JobRoles.Where(r => r.DepartmentId == departmentId).ToListAsync(cancellationToken);
}

internal sealed class DepartmentRepository(NodeDbContext db) : IDepartmentRepository
{
    public Task<Department?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.Departments.SingleOrDefaultAsync(d => d.Id == id, cancellationToken);

    public void Add(Department aggregate) => db.Departments.Add(aggregate);

    public void Remove(Department department) => db.Departments.Remove(department);

    public async Task<IReadOnlyList<Department>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await db.Departments.AsNoTracking().OrderBy(d => d.Name).ToListAsync(cancellationToken);
}

internal sealed class AttendanceDeviceRepository(NodeDbContext db) : IAttendanceDeviceRepository
{
    public Task<AttendanceDevice?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.AttendanceDevices.SingleOrDefaultAsync(d => d.Id == id, cancellationToken);

    public void Add(AttendanceDevice aggregate) => db.AttendanceDevices.Add(aggregate);

    public Task<AttendanceDevice?> GetBySerialNumberAsync(string serialNumber, CancellationToken cancellationToken = default) =>
        db.AttendanceDevices.SingleOrDefaultAsync(d => d.SerialNumber == serialNumber, cancellationToken);

    public async Task<IReadOnlyList<AttendanceDevice>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await db.AttendanceDevices.AsNoTracking().OrderBy(d => d.FirstSeenAtUtc).ToListAsync(cancellationToken);
}

internal sealed class AttendanceLogRepository(NodeDbContext db) : IAttendanceLogRepository
{
    public Task<AttendanceLog?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.AttendanceLogs.SingleOrDefaultAsync(l => l.Id == id, cancellationToken);

    public void Add(AttendanceLog aggregate) => db.AttendanceLogs.Add(aggregate);

    public async Task<IReadOnlyList<AttendanceLog>> GetFromDeviceAsync(
        string serialNumber, DateTime from, DateTime to, CancellationToken cancellationToken = default) =>
        await db.AttendanceLogs.AsNoTracking()
            .Where(l => l.DeviceSerialNumber == serialNumber && l.PunchedAt >= from && l.PunchedAt <= to)
            .ToListAsync(cancellationToken);

    public Task<AttendanceLog?> GetLastReceivedFromDeviceAsync(string serialNumber, CancellationToken cancellationToken = default) =>
        db.AttendanceLogs.AsNoTracking()
            .Where(l => l.DeviceSerialNumber == serialNumber && l.IgnoredAtUtc == null)
            .OrderByDescending(l => l.ReceivedAtUtc).ThenByDescending(l => l.PunchedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<AttendanceLog>> GetBetweenAsync(
        DateTime from, DateTime to, Guid? employeeId = null, CancellationToken cancellationToken = default) =>
        await db.AttendanceLogs.AsNoTracking()
            .Where(l => l.PunchedAt >= from && l.PunchedAt < to && (employeeId == null || l.EmployeeId == employeeId))
            .OrderBy(l => l.PunchedAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<AttendanceLog>> GetUnassignedAsync(string deviceUserId, CancellationToken cancellationToken = default) =>
        await db.AttendanceLogs.Where(l => l.EmployeeId == null && l.DeviceUserId == deviceUserId).ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<string, int>> CountUnassignedAsync(CancellationToken cancellationToken = default) =>
        await db.AttendanceLogs.Where(l => l.EmployeeId == null && l.DeviceUserId != null)
            .GroupBy(l => l.DeviceUserId!)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);
}

internal sealed class DeviceCommandRepository(NodeDbContext db) : IDeviceCommandRepository
{
    public Task<DeviceCommand?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.DeviceCommands.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);

    public void Add(DeviceCommand aggregate) => db.DeviceCommands.Add(aggregate);

    public Task<DeviceCommand?> GetNextAsync(string serialNumber, CancellationToken cancellationToken = default) =>
        db.DeviceCommands.Where(c => c.DeviceSerialNumber == serialNumber && c.SentAtUtc == null).OrderBy(c => c.Number).FirstOrDefaultAsync(cancellationToken);

    public Task<DeviceCommand?> GetByNumberAsync(string serialNumber, int number, CancellationToken cancellationToken = default) =>
        db.DeviceCommands.SingleOrDefaultAsync(c => c.DeviceSerialNumber == serialNumber && c.Number == number, cancellationToken);

    public async Task<int> GetLastNumberAsync(string serialNumber, CancellationToken cancellationToken = default) =>
        await db.DeviceCommands.Where(c => c.DeviceSerialNumber == serialNumber).MaxAsync(c => (int?)c.Number, cancellationToken) ?? 0;

    public async Task<IReadOnlyList<DeviceCommand>> GetRecentAsync(int perDevice, CancellationToken cancellationToken = default) =>
        (await db.DeviceCommands.AsNoTracking().OrderByDescending(c => c.QueuedAtUtc).Take(200).ToListAsync(cancellationToken))
            .GroupBy(c => c.DeviceSerialNumber)
            .SelectMany(g => g.OrderByDescending(c => c.Number).Take(perDevice))
            .ToList();
}

internal sealed class DeviceUserRepository(NodeDbContext db) : IDeviceUserRepository
{
    public Task<DeviceUser?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.DeviceUsers.SingleOrDefaultAsync(u => u.Id == id, cancellationToken);

    public void Add(DeviceUser aggregate) => db.DeviceUsers.Add(aggregate);

    public async Task<IReadOnlyList<DeviceUser>> GetForDeviceAsync(string serialNumber, CancellationToken cancellationToken = default) =>
        await db.DeviceUsers.Where(u => u.DeviceSerialNumber == serialNumber).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<DeviceUser>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await db.DeviceUsers.AsNoTracking().ToListAsync(cancellationToken);
}

internal sealed class DayMarkRepository(NodeDbContext db) : IDayMarkRepository
{
    public Task<DayMark?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.DayMarks.SingleOrDefaultAsync(m => m.Id == id, cancellationToken);

    public void Add(DayMark aggregate) => db.DayMarks.Add(aggregate);

    public void Remove(DayMark mark) => db.DayMarks.Remove(mark);

    public Task<DayMark?> GetAsync(Guid employeeId, DateOnly date, CancellationToken cancellationToken = default) =>
        db.DayMarks.SingleOrDefaultAsync(m => m.EmployeeId == employeeId && m.Date == date, cancellationToken);

    public async Task<IReadOnlyList<DayMark>> GetBetweenAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        await db.DayMarks.AsNoTracking().Where(m => m.Date >= from && m.Date <= to).ToListAsync(cancellationToken);
}

internal sealed class AdministrativePenaltyRepository(NodeDbContext db) : IAdministrativePenaltyRepository
{
    public Task<AdministrativePenalty?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.AdministrativePenalties.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);

    public void Add(AdministrativePenalty aggregate) => db.AdministrativePenalties.Add(aggregate);

    public void Remove(AdministrativePenalty penalty) => db.AdministrativePenalties.Remove(penalty);

    public Task<AdministrativePenalty?> GetAsync(Guid employeeId, DateOnly date, CancellationToken cancellationToken = default) =>
        db.AdministrativePenalties.SingleOrDefaultAsync(p => p.EmployeeId == employeeId && p.Date == date, cancellationToken);

    public async Task<IReadOnlyList<AdministrativePenalty>> GetBetweenAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        await db.AdministrativePenalties.AsNoTracking().Where(p => p.Date >= from && p.Date <= to).ToListAsync(cancellationToken);
}

internal sealed class PublicHolidayRepository(NodeDbContext db) : IPublicHolidayRepository
{
    public Task<PublicHoliday?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.PublicHolidays.SingleOrDefaultAsync(h => h.Id == id, cancellationToken);

    public void Add(PublicHoliday aggregate) => db.PublicHolidays.Add(aggregate);

    public void Remove(PublicHoliday holiday) => db.PublicHolidays.Remove(holiday);

    public async Task<IReadOnlyList<PublicHoliday>> GetBetweenAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        await db.PublicHolidays.AsNoTracking()
            .Where(h => h.FirstDay <= to && h.LastDay >= from)
            .OrderBy(h => h.FirstDay)
            .ToListAsync(cancellationToken);
}

internal sealed class DailyRecordRepository(NodeDbContext db) : IDailyRecordRepository
{
    public Task<DailyRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.DailyRecords.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

    public void Add(DailyRecord aggregate) => db.DailyRecords.Add(aggregate);

    public async Task<IReadOnlyList<DailyRecord>> GetForEmployeeAsync(
        Guid employeeId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        await db.DailyRecords.Where(r => r.EmployeeId == employeeId && r.Date >= from && r.Date <= to).ToListAsync(cancellationToken);
}

internal sealed class PayrollAdjustmentRepository(NodeDbContext db) : IPayrollAdjustmentRepository
{
    public Task<PayrollAdjustment?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.PayrollAdjustments.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);

    public void Add(PayrollAdjustment aggregate) => db.PayrollAdjustments.Add(aggregate);

    public async Task<IReadOnlyList<PayrollAdjustment>> GetForMonthAsync(DateOnly month, CancellationToken cancellationToken = default) =>
        await db.PayrollAdjustments.AsNoTracking().Where(a => a.Month == month).ToListAsync(cancellationToken);
}

// Tracked: a share is taken back in place.
internal sealed class AdvanceAllocationRepository(NodeDbContext db) : IAdvanceAllocationRepository
{
    public Task<AdvanceAllocation?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.AdvanceAllocations.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);

    public void Add(AdvanceAllocation aggregate) => db.AdvanceAllocations.Add(aggregate);

    public async Task<IReadOnlyList<AdvanceAllocation>> GetForAdvancesAsync(
        IReadOnlyCollection<Guid> sourceIds, CancellationToken cancellationToken = default) =>
        await db.AdvanceAllocations
            .Where(a => (a.SourceTransactionId != null && sourceIds.Contains(a.SourceTransactionId.Value))
                || (a.SourceAdvanceId != null && sourceIds.Contains(a.SourceAdvanceId.Value)))
            .OrderBy(a => a.AllocatedAtUtc).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<AdvanceAllocation>> GetForPayrollMonthAsync(
        DateOnly month, Guid? employeeId = null, CancellationToken cancellationToken = default) =>
        await db.AdvanceAllocations.AsNoTracking().Where(a => a.PayrollMonth == month && (employeeId == null || a.EmployeeId == employeeId))
            .OrderBy(a => a.Date).ThenBy(a => a.AllocatedAtUtc).ToListAsync(cancellationToken);
}

// Tracked: one is voided in place.
internal sealed class HrAdvanceRepository(NodeDbContext db) : IHrAdvanceRepository
{
    public Task<HrAdvance?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.HrAdvances.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);

    public void Add(HrAdvance aggregate) => db.HrAdvances.Add(aggregate);

    public async Task<IReadOnlyList<HrAdvance>> GetForPayrollMonthAsync(
        DateOnly month, Guid? employeeId = null, CancellationToken cancellationToken = default) =>
        await db.HrAdvances.AsNoTracking().Where(a => a.PayrollMonth == month && (employeeId == null || a.EmployeeId == employeeId))
            .OrderBy(a => a.Date).ThenBy(a => a.RecordedAtUtc).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<HrAdvance>> GetPendingAsync(CancellationToken cancellationToken = default) =>
        await db.HrAdvances.AsNoTracking().Where(a => a.EmployeeId == null && a.VoidedAtUtc == null)
            .OrderByDescending(a => a.Date).ThenByDescending(a => a.RecordedAtUtc).ToListAsync(cancellationToken);
}

// Tracked: a draft is worked out again in place.
internal sealed class PayrollSlipRepository(NodeDbContext db) : IPayrollSlipRepository
{
    public Task<PayrollSlip?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.PayrollSlips.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);

    public void Add(PayrollSlip aggregate) => db.PayrollSlips.Add(aggregate);

    public async Task<PayrollSlip?> GetAsync(Guid employeeId, DateOnly month, CancellationToken cancellationToken = default) =>
        db.PayrollSlips.Local.FirstOrDefault(s => s.EmployeeId == employeeId && s.Month == month)
        ?? await db.PayrollSlips.SingleOrDefaultAsync(s => s.EmployeeId == employeeId && s.Month == month, cancellationToken);

    public async Task<IReadOnlyList<PayrollSlip>> GetForMonthAsync(DateOnly month, CancellationToken cancellationToken = default) =>
        await db.PayrollSlips.Where(s => s.Month == month).ToListAsync(cancellationToken);
}

internal sealed class AttendanceSettingsStore(NodeDbContext db) : IAttendanceSettings
{
    public async Task<AttendancePolicy> GetAsync(CancellationToken cancellationToken) =>
        (await db.HrSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken))?.ToPolicy() ?? AttendancePolicy.Default;

    public async Task SaveAsync(AttendancePolicy policy, CancellationToken cancellationToken)
    {
        var settings = await db.HrSettings.SingleOrDefaultAsync(cancellationToken);
        if (settings is null)
        {
            settings = new HrSettings();
            db.HrSettings.Add(settings);
        }

        settings.GraceMinutes = policy.GraceMinutes;
        settings.DoubleScanMinutes = policy.DoubleScanMinutes;
        await db.SaveChangesAsync(cancellationToken);
    }
}
