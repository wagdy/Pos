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

    public async Task<IReadOnlyList<AttendanceLog>> GetBetweenAsync(
        DateTime from, DateTime to, Guid? employeeId = null, CancellationToken cancellationToken = default) =>
        await db.AttendanceLogs.AsNoTracking()
            .Where(l => l.PunchedAt >= from && l.PunchedAt < to && (employeeId == null || l.EmployeeId == employeeId))
            .OrderBy(l => l.PunchedAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<AttendanceLog>> GetUnassignedAsync(string deviceUserId, CancellationToken cancellationToken = default) =>
        await db.AttendanceLogs.Where(l => l.EmployeeId == null && l.DeviceUserId == deviceUserId).ToListAsync(cancellationToken);
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
