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

internal sealed class DailyRecordConfiguration : IEntityTypeConfiguration<DailyRecord>
{
    public void Configure(EntityTypeBuilder<DailyRecord> builder)
    {
        builder.ToTable("DailyRecords", NodeDbContext.HrSchema);
        builder.Property(r => r.Status).IsEnumName();
        builder.Property(r => r.FirstIn).HasColumnType("timestamp without time zone");
        builder.Property(r => r.LastOut).HasColumnType("timestamp without time zone");
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

internal sealed class PayrollSlipConfiguration : IEntityTypeConfiguration<PayrollSlip>
{
    public void Configure(EntityTypeBuilder<PayrollSlip> builder)
    {
        builder.ToTable("PayrollSlips", NodeDbContext.HrSchema);
        builder.Property(s => s.Status).IsEnumName();
        builder.Property(s => s.FinalizedBy).HasMaxLength(200);
        builder.Ignore(s => s.IsFinalized);
        // The figures in the slip's own row, a column each.
        builder.ComplexProperty(s => s.Figures);
        builder.HasOne<Employee>().WithMany().HasForeignKey(s => s.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(s => new { s.EmployeeId, s.Month }).IsUnique();
        builder.HasIndex(s => s.Month);
    }
}
