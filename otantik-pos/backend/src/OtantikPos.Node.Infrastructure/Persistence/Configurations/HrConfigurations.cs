using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OtantikPos.Hr.Domain.Attendance;
using OtantikPos.Hr.Domain.Employees;
using OtantikPos.Hr.Domain.Payroll;

namespace OtantikPos.Node.Infrastructure.Persistence.Configurations;

// The hr schema: employees, their punches and days, and their monthly pay. Punch times are the
// device's wall clock, so they are stored without a time zone.

internal sealed class EmployeeConfiguration : IEntityTypeConfiguration<Employee>
{
    public void Configure(EntityTypeBuilder<Employee> builder)
    {
        builder.ToTable("Employees", NodeDbContext.HrSchema);
        builder.Property(e => e.Name).HasMaxLength(Employee.MaxNameLength);
        builder.Property(e => e.EmploymentType).IsEnumName();
        builder.Ignore(e => e.IsDaily);
        builder.HasOne<Department>().WithMany().HasForeignKey(e => e.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<JobRole>().WithMany().HasForeignKey(e => e.JobRoleId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(e => e.JobTitle).HasMaxLength(Employee.MaxJobTitleLength);
        // By name, "Friday, Saturday", as the other enums are stored.
        builder.Property(e => e.DaysOff).HasConversion<string>().HasMaxLength(100);
        builder.Property(e => e.DeviceUserId).HasMaxLength(Employee.MaxDeviceUserIdLength);
        builder.Property(e => e.StaffMemberId).HasMaxLength(450);
        builder.Ignore(e => e.DailyRate);
        builder.Ignore(e => e.HourlyRate);
        builder.Ignore(e => e.MinuteRate);
        // One employee to a user number on the device, and to a till account.
        builder.HasIndex(e => e.DeviceUserId).IsUnique().HasFilter("\"DeviceUserId\" IS NOT NULL");
        builder.HasIndex(e => e.StaffMemberId).IsUnique().HasFilter("\"StaffMemberId\" IS NOT NULL");
    }
}

internal sealed class DepartmentConfiguration : IEntityTypeConfiguration<Department>
{
    public void Configure(EntityTypeBuilder<Department> builder)
    {
        builder.ToTable("Departments", NodeDbContext.HrSchema);
        builder.Property(d => d.Name).HasMaxLength(Department.MaxNameLength);
        builder.Ignore(d => d.IsGeneral);
        // There from the start, for the employees already entered.
        builder.HasData(new { Id = Department.GeneralId, Name = "General", CreatedAtUtc = new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc) });
    }
}

internal sealed class JobRoleConfiguration : IEntityTypeConfiguration<JobRole>
{
    public void Configure(EntityTypeBuilder<JobRole> builder)
    {
        builder.ToTable("JobRoles", NodeDbContext.HrSchema);
        builder.Property(r => r.Name).HasMaxLength(JobRole.MaxNameLength);
        builder.HasOne<Department>().WithMany().HasForeignKey(r => r.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        // One name to a department.
        builder.HasIndex(r => new { r.DepartmentId, r.Name }).IsUnique();
    }
}

internal sealed class AttendanceDeviceConfiguration : IEntityTypeConfiguration<AttendanceDevice>
{
    public void Configure(EntityTypeBuilder<AttendanceDevice> builder)
    {
        builder.ToTable("AttendanceDevices", NodeDbContext.HrSchema);
        builder.Property(d => d.SerialNumber).HasMaxLength(AttendanceDevice.MaxSerialNumberLength);
        builder.Property(d => d.Name).HasMaxLength(AttendanceDevice.MaxNameLength);
        builder.Property(d => d.LastAddress).HasMaxLength(64);
        builder.Property(d => d.ApprovedBy).HasMaxLength(200);
        builder.Property(d => d.AttendanceStamp).HasMaxLength(32);
        builder.Ignore(d => d.IsApproved);
        builder.HasIndex(d => d.SerialNumber).IsUnique();
    }
}

internal sealed class AttendanceLogConfiguration : IEntityTypeConfiguration<AttendanceLog>
{
    public void Configure(EntityTypeBuilder<AttendanceLog> builder)
    {
        builder.ToTable("AttendanceLogs", NodeDbContext.HrSchema);
        builder.Property(l => l.Source).IsEnumName();
        builder.Property(l => l.DeviceSerialNumber).HasMaxLength(AttendanceDevice.MaxSerialNumberLength);
        // As long as the device sends it: a number nobody has yet is kept as it came.
        builder.Property(l => l.DeviceUserId).HasMaxLength(32);
        builder.Property(l => l.PunchedAt).HasColumnType("timestamp without time zone");
        builder.Property(l => l.RecordedBy).HasMaxLength(200);
        builder.Property(l => l.Reason).HasMaxLength(AttendanceLog.MaxReasonLength);
        builder.Property(l => l.IgnoredBy).HasMaxLength(200);
        builder.Property(l => l.LateSettledBy).HasMaxLength(200);
        builder.Property(l => l.IgnoreReason).HasMaxLength(AttendanceLog.MaxReasonLength);
        builder.Ignore(l => l.IsIgnored);
        builder.Ignore(l => l.Counts);
        builder.HasOne<Employee>().WithMany().HasForeignKey(l => l.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        // A device sends a punch again after a lost answer or a restart: it is kept once.
        builder.HasIndex(l => new { l.DeviceSerialNumber, l.DeviceUserId, l.PunchedAt }).IsUnique().HasFilter("\"DeviceSerialNumber\" IS NOT NULL");
        builder.HasIndex(l => new { l.EmployeeId, l.PunchedAt });
        // The punches waiting for their user number to be linked to someone.
        builder.HasIndex(l => l.DeviceUserId).HasFilter("\"EmployeeId\" IS NULL");
    }
}

internal sealed class DayMarkConfiguration : IEntityTypeConfiguration<DayMark>
{
    public void Configure(EntityTypeBuilder<DayMark> builder)
    {
        builder.ToTable("DayMarks", NodeDbContext.HrSchema);
        builder.Property(m => m.Kind).IsEnumName();
        builder.Property(m => m.Notes).HasMaxLength(DayMark.MaxNotesLength);
        builder.Property(m => m.MarkedBy).HasMaxLength(200);
        builder.HasOne<Employee>().WithMany().HasForeignKey(m => m.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(m => new { m.EmployeeId, m.Date }).IsUnique();
    }
}

internal sealed class AdministrativePenaltyConfiguration : IEntityTypeConfiguration<AdministrativePenalty>
{
    public void Configure(EntityTypeBuilder<AdministrativePenalty> builder)
    {
        builder.ToTable("AdministrativePenalties", NodeDbContext.HrSchema);
        builder.Property(p => p.Hours).HasPrecision(5, 2);
        builder.Property(p => p.Reason).HasMaxLength(AdministrativePenalty.MaxReasonLength);
        builder.Property(p => p.SetBy).HasMaxLength(200);
        builder.HasOne<Employee>().WithMany().HasForeignKey(p => p.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        // One a day: changed, not added to.
        builder.HasIndex(p => new { p.EmployeeId, p.Date }).IsUnique();
    }
}

internal sealed class DeviceCommandConfiguration : IEntityTypeConfiguration<DeviceCommand>
{
    public void Configure(EntityTypeBuilder<DeviceCommand> builder)
    {
        builder.ToTable("DeviceCommands", NodeDbContext.HrSchema);
        builder.Property(c => c.DeviceSerialNumber).HasMaxLength(AttendanceDevice.MaxSerialNumberLength);
        builder.Property(c => c.Kind).IsEnumName();
        builder.Property(c => c.Text).HasMaxLength(200);
        builder.Property(c => c.QueuedBy).HasMaxLength(200);
        builder.Ignore(c => c.Line);
        // The device knows a command by its number alone.
        builder.HasIndex(c => new { c.DeviceSerialNumber, c.Number }).IsUnique();
    }
}

internal sealed class DeviceUserConfiguration : IEntityTypeConfiguration<DeviceUser>
{
    public void Configure(EntityTypeBuilder<DeviceUser> builder)
    {
        builder.ToTable("DeviceUsers", NodeDbContext.HrSchema);
        builder.Property(u => u.DeviceSerialNumber).HasMaxLength(AttendanceDevice.MaxSerialNumberLength);
        builder.Property(u => u.DeviceUserId).HasMaxLength(DeviceUser.MaxDeviceUserIdLength);
        builder.Property(u => u.Name).HasMaxLength(DeviceUser.MaxNameLength);
        builder.HasIndex(u => new { u.DeviceSerialNumber, u.DeviceUserId }).IsUnique();
    }
}

internal sealed class PublicHolidayConfiguration : IEntityTypeConfiguration<PublicHoliday>
{
    public void Configure(EntityTypeBuilder<PublicHoliday> builder)
    {
        builder.ToTable("PublicHolidays", NodeDbContext.HrSchema);
        builder.Property(h => h.Name).HasMaxLength(PublicHoliday.MaxNameLength);
        builder.Property(h => h.SetBy).HasMaxLength(200);
        builder.Ignore(h => h.Days);
        // Read by the days they cover.
        builder.HasIndex(h => new { h.FirstDay, h.LastDay });
    }
}

internal sealed class DailyRecordConfiguration : IEntityTypeConfiguration<DailyRecord>
{
    public void Configure(EntityTypeBuilder<DailyRecord> builder)
    {
        builder.ToTable("DailyRecords", NodeDbContext.HrSchema);
        builder.Property(r => r.Status).IsEnumName();
        builder.Property(r => r.FirstIn).HasColumnType("timestamp without time zone");
        builder.Property(r => r.LastOut).HasColumnType("timestamp without time zone");
        builder.Property(r => r.AdministrativePenaltyHours).HasPrecision(5, 2);
        builder.HasOne<Employee>().WithMany().HasForeignKey(r => r.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => new { r.EmployeeId, r.Date }).IsUnique();
    }
}

internal sealed class PayrollAdjustmentConfiguration : IEntityTypeConfiguration<PayrollAdjustment>
{
    public void Configure(EntityTypeBuilder<PayrollAdjustment> builder)
    {
        builder.ToTable("PayrollAdjustments", NodeDbContext.HrSchema);
        builder.Property(a => a.Kind).IsEnumName();
        builder.Property(a => a.Source).IsEnumName();
        builder.Property(a => a.Reason).HasMaxLength(PayrollAdjustment.MaxReasonLength);
        builder.Property(a => a.RecordedBy).HasMaxLength(200);
        builder.Property(a => a.ApprovedBy).HasMaxLength(200);
        builder.Property(a => a.VoidedBy).HasMaxLength(200);
        builder.Ignore(a => a.IsVoided);
        builder.HasOne<Employee>().WithMany().HasForeignKey(a => a.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(a => new { a.EmployeeId, a.Month });
    }
}

internal sealed class AdvanceAllocationConfiguration : IEntityTypeConfiguration<AdvanceAllocation>
{
    public void Configure(EntityTypeBuilder<AdvanceAllocation> builder)
    {
        // A share of one or the other: a safe entry, or an HR advance paid outside the till.
        builder.ToTable("AdvanceAllocations", NodeDbContext.HrSchema, t =>
            t.HasCheckConstraint("CK_AdvanceAllocations_OneSource", "(\"SourceTransactionId\" IS NULL) <> (\"SourceAdvanceId\" IS NULL)"));
        builder.Property(a => a.Amount).HasPrecision(18, 2);
        builder.Ignore(a => a.SourceId);
        builder.Ignore(a => a.OutsideTill);
        builder.Property(a => a.Notes).HasMaxLength(AdvanceAllocation.MaxNotesLength);
        builder.Property(a => a.AllocatedBy).HasMaxLength(200);
        builder.Property(a => a.VoidedBy).HasMaxLength(200);
        builder.Ignore(a => a.IsVoided);
        builder.HasOne<Employee>().WithMany().HasForeignKey(a => a.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<OtantikPos.Inventory.Domain.Treasury.SafeTransaction>().WithMany().HasForeignKey(a => a.SourceTransactionId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<HrAdvance>().WithMany().HasForeignKey(a => a.SourceAdvanceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(a => a.SourceTransactionId);
        builder.HasIndex(a => a.SourceAdvanceId);
        builder.HasIndex(a => new { a.EmployeeId, a.PayrollMonth });
    }
}

internal sealed class HrAdvanceConfiguration : IEntityTypeConfiguration<HrAdvance>
{
    public void Configure(EntityTypeBuilder<HrAdvance> builder)
    {
        builder.ToTable("HrAdvances", NodeDbContext.HrSchema);
        builder.Property(a => a.Amount).HasPrecision(18, 2);
        builder.Property(a => a.Notes).HasMaxLength(HrAdvance.MaxNotesLength);
        builder.Property(a => a.RecordedBy).HasMaxLength(200);
        builder.Property(a => a.ApprovedBy).HasMaxLength(200);
        builder.Property(a => a.VoidedBy).HasMaxLength(200);
        builder.Ignore(a => a.IsVoided);
        builder.Ignore(a => a.IsPending);
        builder.HasOne<Employee>().WithMany().HasForeignKey(a => a.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(a => new { a.EmployeeId, a.PayrollMonth }).HasFilter("\"EmployeeId\" IS NOT NULL");
    }
}

internal sealed class PayrollSlipConfiguration : IEntityTypeConfiguration<PayrollSlip>
{
    public void Configure(EntityTypeBuilder<PayrollSlip> builder)
    {
        builder.ToTable("PayrollSlips", NodeDbContext.HrSchema);
        builder.Property(s => s.Status).IsEnumName();
        builder.Property(s => s.FinalizedBy).HasMaxLength(200);
        builder.Ignore(s => s.IsFinalized);
        // The figures in the slip's own row, a column each.
        builder.ComplexProperty(s => s.Figures, figures => figures.Property(f => f.EmploymentType).HasConversion<string>().HasMaxLength(32));
        builder.HasOne<Employee>().WithMany().HasForeignKey(s => s.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(s => new { s.EmployeeId, s.Month }).IsUnique();
        builder.HasIndex(s => s.Month);
    }
}
