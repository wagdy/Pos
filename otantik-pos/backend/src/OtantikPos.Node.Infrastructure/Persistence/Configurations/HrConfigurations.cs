using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OtantikPos.Hr.Domain.Access;
using OtantikPos.Hr.Domain.Attendance;
using OtantikPos.Hr.Domain.Custody;
using OtantikPos.Hr.Domain.Employees;
using OtantikPos.Hr.Domain.Leaves;
using OtantikPos.Hr.Domain.Performance;
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
        builder.Property(e => e.NationalId).HasMaxLength(Employee.MaxNationalIdLength);
        builder.Property(e => e.MobileNumber).HasMaxLength(Employee.MaxMobileNumberLength);
        builder.Property(e => e.IdPhotoFileName).HasMaxLength(Employee.MaxIdPhotoFileNameLength);
        builder.Property(e => e.IdPhotoContentType).HasMaxLength(100);
        builder.Property(e => e.IdPhotoUploadedBy).HasMaxLength(200);
        builder.Ignore(e => e.HasIdPhoto);
        builder.Ignore(e => e.DailyRate);
        builder.Ignore(e => e.HourlyRate);
        builder.Ignore(e => e.MinuteRate);
        // One employee to a user number on the device, and to a till account.
        builder.HasIndex(e => e.DeviceUserId).IsUnique().HasFilter("\"DeviceUserId\" IS NOT NULL");
        builder.HasIndex(e => e.StaffMemberId).IsUnique().HasFilter("\"StaffMemberId\" IS NOT NULL");
        // One employee to an ID card, and to a mobile number: the portal signs in by it.
        builder.HasIndex(e => e.NationalId).IsUnique().HasFilter("\"NationalId\" IS NOT NULL");
        builder.HasIndex(e => e.MobileNumber).IsUnique().HasFilter("\"MobileNumber\" IS NOT NULL");
        builder.Property(e => e.PinHash).HasMaxLength(200);
        builder.Ignore(e => e.HasPin);
        // By name, "Now, Attendance", as the days off are; "None" for none.
        builder.Property(e => e.HrTabs).HasConversion<string>().HasMaxLength(200);
    }
}

internal sealed class StaffHrAccessConfiguration : IEntityTypeConfiguration<StaffHrAccess>
{
    public void Configure(EntityTypeBuilder<StaffHrAccess> builder)
    {
        builder.ToTable("StaffHrAccess", NodeDbContext.HrSchema);
        builder.Property(a => a.StaffId).HasMaxLength(StaffHrAccess.MaxStaffIdLength);
        builder.HasIndex(a => a.StaffId).IsUnique();
        builder.Property(a => a.Tabs).HasConversion<string>().HasMaxLength(200);
        builder.Property(a => a.SetBy).HasMaxLength(200);
    }
}

internal sealed class AssetCategoryConfiguration : IEntityTypeConfiguration<AssetCategory>
{
    public void Configure(EntityTypeBuilder<AssetCategory> builder)
    {
        builder.ToTable("AssetCategories", NodeDbContext.HrSchema);
        builder.Property(c => c.Name).HasMaxLength(AssetCategory.MaxNameLength);
        builder.HasIndex(c => c.Name).IsUnique();
    }
}

internal sealed class AssetItemConfiguration : IEntityTypeConfiguration<AssetItem>
{
    public void Configure(EntityTypeBuilder<AssetItem> builder)
    {
        builder.ToTable("AssetItems", NodeDbContext.HrSchema, t =>
        {
            t.HasCheckConstraint("CK_AssetItems_Quantity", "\"Quantity\" >= 0");
            // Bought on an invoice: its line said; entered in HR: none.
            t.HasCheckConstraint(
                "CK_AssetItems_Source",
                "(\"Origin\" = 'FinancialPurchase') = (\"SourceInvoiceId\" IS NOT NULL AND \"SourceInvoiceLineId\" IS NOT NULL)");
        });
        builder.Property(a => a.Name).HasMaxLength(AssetItem.MaxNameLength);
        builder.Property(a => a.Notes).HasMaxLength(AssetItem.MaxNotesLength);
        builder.Property(a => a.CreatedBy).HasMaxLength(200);
        builder.Property(a => a.UnitCost).HasPrecision(18, 2);
        builder.Property(a => a.Origin).IsEnumName();
        builder.Property(a => a.Status).IsEnumName();
        builder.Ignore(a => a.TotalCost);
        builder.Ignore(a => a.IsGone);
        builder.HasOne<AssetCategory>().WithMany().HasForeignKey(a => a.CategoryId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Employee>().WithMany().HasForeignKey(a => a.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<OtantikPos.Inventory.Domain.Purchases.PurchaseInvoice>().WithMany().HasForeignKey(a => a.SourceInvoiceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(a => a.EmployeeId);
        // A posted line makes one asset, however often it is read.
        builder.HasIndex(a => a.SourceInvoiceLineId).IsUnique().HasFilter("\"SourceInvoiceLineId\" IS NOT NULL");
    }
}

internal sealed class AssetTransactionConfiguration : IEntityTypeConfiguration<AssetTransaction>
{
    public void Configure(EntityTypeBuilder<AssetTransaction> builder)
    {
        builder.ToTable("AssetTransactions", NodeDbContext.HrSchema, t =>
        {
            t.HasCheckConstraint("CK_AssetTransactions_Quantity", "\"Quantity\" > 0");
            // A check-out says why; a check-in, nothing.
            t.HasCheckConstraint("CK_AssetTransactions_Reason", "(\"Type\" = 'CheckOut') = (\"Reason\" IS NOT NULL)");
        });
        builder.Property(t => t.Type).IsEnumName();
        builder.Property(t => t.Reason).IsEnumName();
        builder.Property(t => t.Notes).HasMaxLength(AssetItem.MaxNotesLength);
        builder.Property(t => t.RecordedBy).HasMaxLength(200);
        builder.Property(t => t.UnitCost).HasPrecision(18, 2);
        builder.Ignore(t => t.Cost);
        builder.Ignore(t => t.RoutedToWaste);
        builder.HasOne<AssetItem>().WithMany().HasForeignKey(t => t.AssetItemId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Employee>().WithMany().HasForeignKey(t => t.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(t => new { t.AssetItemId, t.Date });
        builder.HasIndex(t => t.Date);
        // The breakage entry it made (inventory.BreakageEntries names it back, with a key).
        builder.HasIndex(t => t.WasteEntryId).IsUnique().HasFilter("\"WasteEntryId\" IS NOT NULL");
    }
}

internal sealed class EmployeeIdPhotoConfiguration : IEntityTypeConfiguration<Hr.EmployeeIdPhoto>
{
    public void Configure(EntityTypeBuilder<Hr.EmployeeIdPhoto> builder)
    {
        builder.ToTable("EmployeeIdPhotos", NodeDbContext.HrSchema);
        builder.HasKey(p => p.EmployeeId);
        builder.HasOne<Employee>().WithOne().HasForeignKey<Hr.EmployeeIdPhoto>(p => p.EmployeeId).OnDelete(DeleteBehavior.Cascade);
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
        builder.ToTable("DayMarks", NodeDbContext.HrSchema, t =>
            // Unpaid leave is an approved leave request's.
            t.HasCheckConstraint("CK_DayMarks_UnpaidLeave", "\"Kind\" <> 'UnpaidLeave' OR \"LeaveRequestId\" IS NOT NULL"));
        builder.Property(m => m.Kind).IsEnumName();
        builder.Property(m => m.Notes).HasMaxLength(DayMark.MaxNotesLength);
        builder.Property(m => m.MarkedBy).HasMaxLength(200);
        builder.HasOne<Employee>().WithMany().HasForeignKey(m => m.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<LeaveRequest>().WithMany().HasForeignKey(m => m.LeaveRequestId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(m => m.LeaveRequestId).HasFilter("\"LeaveRequestId\" IS NOT NULL");
        builder.HasIndex(m => new { m.EmployeeId, m.Date }).IsUnique();
    }
}

internal sealed class AdministrativePenaltyConfiguration : IEntityTypeConfiguration<AdministrativePenalty>
{
    public void Configure(EntityTypeBuilder<AdministrativePenalty> builder)
    {
        builder.ToTable("AdministrativePenalties", NodeDbContext.HrSchema, t =>
        {
            // Hours, or an amount of money: one or the other.
            t.HasCheckConstraint("CK_AdministrativePenalties_HoursOrAmount",
                "(\"Hours\" > 0 AND \"Amount\" IS NULL) OR (\"Hours\" = 0 AND \"Amount\" > 0)");
            // A disciplinary action's deduction is an amount.
            t.HasCheckConstraint("CK_AdministrativePenalties_Disciplinary", "\"DisciplinaryActionId\" IS NULL OR \"Amount\" IS NOT NULL");
        });
        builder.Property(p => p.Hours).HasPrecision(5, 2);
        builder.Property(p => p.Amount).HasPrecision(18, 2);
        builder.Property(p => p.Reason).HasMaxLength(AdministrativePenalty.MaxReasonLength);
        builder.Property(p => p.SetBy).HasMaxLength(200);
        builder.HasOne<Employee>().WithMany().HasForeignKey(p => p.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<OtantikPos.Hr.Domain.Performance.DisciplinaryAction>().WithMany().HasForeignKey(p => p.DisciplinaryActionId).OnDelete(DeleteBehavior.Restrict);
        // A manager's own, one a day: changed, not added to. A disciplinary action's beside it, one to an action.
        builder.HasIndex(p => new { p.EmployeeId, p.Date }).IsUnique().HasFilter("\"DisciplinaryActionId\" IS NULL");
        builder.HasIndex(p => p.DisciplinaryActionId).IsUnique().HasFilter("\"DisciplinaryActionId\" IS NOT NULL");
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
        builder.Property(r => r.AdministrativePenaltyAmount).HasPrecision(18, 2);
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

internal sealed class LeaveTypeConfiguration : IEntityTypeConfiguration<LeaveType>
{
    public void Configure(EntityTypeBuilder<LeaveType> builder)
    {
        builder.ToTable("LeaveTypes", NodeDbContext.HrSchema, t =>
        {
            t.HasCheckConstraint("CK_LeaveTypes_Days", $"\"DefaultDaysPerYear\" BETWEEN 0 AND {LeaveType.MaxDaysPerYear}");
            // Unpaid leave has no days a year.
            t.HasCheckConstraint("CK_LeaveTypes_Unpaid", "\"IsPaid\" OR \"DefaultDaysPerYear\" = 0");
        });
        builder.Property(l => l.Name).HasMaxLength(LeaveType.MaxNameLength);
        builder.HasIndex(l => l.Name).IsUnique();
    }
}

internal sealed class EmployeeLeaveBalanceConfiguration : IEntityTypeConfiguration<EmployeeLeaveBalance>
{
    public void Configure(EntityTypeBuilder<EmployeeLeaveBalance> builder)
    {
        builder.ToTable("EmployeeLeaveBalances", NodeDbContext.HrSchema, t =>
        {
            t.HasCheckConstraint("CK_EmployeeLeaveBalances_Days",
                $"\"TotalAllocatedDays\" BETWEEN 0 AND {EmployeeLeaveBalance.MaxDays} AND \"UsedDays\" BETWEEN 0 AND \"TotalAllocatedDays\"");
            t.HasCheckConstraint("CK_EmployeeLeaveBalances_Year", $"\"Year\" BETWEEN {EmployeeLeaveBalance.FirstYear} AND {EmployeeLeaveBalance.LastYear}");
        });
        builder.Property(b => b.AllocatedBy).HasMaxLength(200);
        // Half days.
        builder.Property(b => b.TotalAllocatedDays).HasPrecision(4, 1);
        builder.Property(b => b.UsedDays).HasPrecision(4, 1);
        builder.Ignore(b => b.RemainingDays);
        builder.HasOne<Employee>().WithMany().HasForeignKey(b => b.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<LeaveType>().WithMany().HasForeignKey(b => b.LeaveTypeId).OnDelete(DeleteBehavior.Restrict);
        // One to an employee, a type and a year.
        builder.HasIndex(b => new { b.EmployeeId, b.LeaveTypeId, b.Year }).IsUnique();
        builder.HasIndex(b => b.Year);
    }
}

internal sealed class LeaveRequestConfiguration : IEntityTypeConfiguration<LeaveRequest>
{
    public void Configure(EntityTypeBuilder<LeaveRequest> builder)
    {
        builder.ToTable("LeaveRequests", NodeDbContext.HrSchema, t =>
        {
            t.HasCheckConstraint("CK_LeaveRequests_Dates",
                $"\"EndDate\" >= \"StartDate\" AND date_part('year', \"EndDate\") = date_part('year', \"StartDate\") AND \"EndDate\" - \"StartDate\" < {LeaveRequest.MaxDays}");
            t.HasCheckConstraint("CK_LeaveRequests_Days", "\"TotalDaysRequested\" > 0 AND \"TotalDaysRequested\" <= \"EndDate\" - \"StartDate\" + 1");
            // Decided: by someone, when; waiting: by no one yet.
            t.HasCheckConstraint("CK_LeaveRequests_Decision",
                "(\"Status\" = 'Pending') = (\"DecidedById\" IS NULL AND \"DecidedAtUtc\" IS NULL)");
            // Cancelled from a day by someone, no more days given back than it took: all of them
            // when none of it stands.
            t.HasCheckConstraint("CK_LeaveRequests_Cancellation",
                "(\"CancelledFrom\" IS NULL) = (\"CancelledAtUtc\" IS NULL) AND (\"CancelledFrom\" IS NULL OR \"Status\" IN ('Approved', 'Cancelled')) " +
                "AND (\"Status\" <> 'Cancelled' OR (\"CancelledFrom\" IS NOT NULL AND \"CancelledDays\" = \"TotalDaysRequested\")) " +
                "AND \"CancelledDays\" BETWEEN 0 AND \"TotalDaysRequested\"");
        });
        builder.Property(r => r.Status).IsEnumName();
        builder.Property(r => r.Reason).HasMaxLength(LeaveRequest.MaxReasonLength);
        builder.Property(r => r.ManagerNotes).HasMaxLength(LeaveRequest.MaxNotesLength);
        builder.Property(r => r.RequestedBy).HasMaxLength(200);
        builder.Property(r => r.DecidedById).HasMaxLength(450);
        builder.Property(r => r.DecidedBy).HasMaxLength(200);
        builder.Property(r => r.CancelledById).HasMaxLength(450);
        builder.Property(r => r.CancelledBy).HasMaxLength(200);
        builder.Property(r => r.CancellationNotes).HasMaxLength(LeaveRequest.MaxNotesLength);
        builder.Ignore(r => r.Year);
        builder.Ignore(r => r.IsPending);
        builder.Ignore(r => r.DaysTaken);
        builder.HasOne<Employee>().WithMany().HasForeignKey(r => r.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<LeaveType>().WithMany().HasForeignKey(r => r.LeaveTypeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => new { r.EmployeeId, r.StartDate });
        // The waiting ones, for the managers' list.
        builder.HasIndex(r => r.Status).HasFilter("\"Status\" = 'Pending'");
    }
}

internal sealed class PerformanceKpiConfiguration : IEntityTypeConfiguration<PerformanceKpi>
{
    public void Configure(EntityTypeBuilder<PerformanceKpi> builder)
    {
        builder.ToTable("PerformanceKpis", NodeDbContext.HrSchema);
        builder.Property(k => k.Name).HasMaxLength(PerformanceKpi.MaxNameLength);
        builder.HasIndex(k => k.Name).IsUnique();
    }
}

internal sealed class PerformanceEvaluationConfiguration : IEntityTypeConfiguration<PerformanceEvaluation>
{
    public void Configure(EntityTypeBuilder<PerformanceEvaluation> builder)
    {
        builder.ToTable("PerformanceEvaluations", NodeDbContext.HrSchema, t =>
            t.HasCheckConstraint("CK_PerformanceEvaluations_Total", "\"TotalScore\" BETWEEN 20 AND 100"));
        builder.Property(e => e.TotalScore).HasPrecision(4, 1);
        builder.Property(e => e.Notes).HasMaxLength(PerformanceEvaluation.MaxNotesLength);
        builder.Property(e => e.ReviewerId).HasMaxLength(450);
        builder.Property(e => e.ReviewerName).HasMaxLength(200);
        builder.HasOne<Employee>().WithMany().HasForeignKey(e => e.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(e => new { e.EmployeeId, e.Date });
        // The KPIs it scored (KpiDetails), a row each.
        builder.OwnsMany(e => e.Scores, score =>
        {
            score.ToTable("EvaluationScores", NodeDbContext.HrSchema, t => t.HasCheckConstraint("CK_EvaluationScores_Score",
                $"\"Score\" BETWEEN {PerformanceEvaluation.MinScore} AND {PerformanceEvaluation.MaxScore}"));
            score.WithOwner().HasForeignKey("EvaluationId");
            score.Property<int>("Id");
            score.HasKey("Id");
            score.Property(s => s.KpiName).HasMaxLength(PerformanceKpi.MaxNameLength);
            score.HasOne<PerformanceKpi>().WithMany().HasForeignKey(s => s.KpiId).OnDelete(DeleteBehavior.Restrict);
            score.HasIndex("EvaluationId", nameof(KpiScore.KpiId)).IsUnique();
        });
        builder.Navigation(e => e.Scores).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class DisciplinaryActionConfiguration : IEntityTypeConfiguration<DisciplinaryAction>
{
    public void Configure(EntityTypeBuilder<DisciplinaryAction> builder)
    {
        builder.ToTable("DisciplinaryActions", NodeDbContext.HrSchema, t =>
        {
            // A penalty deduction has its amount; nothing else has one.
            t.HasCheckConstraint("CK_DisciplinaryActions_Penalty",
                "(\"SeverityLevel\" = 'PenaltyDeduction') = (\"PenaltyAmount\" IS NOT NULL) AND (\"PenaltyAmount\" IS NULL OR \"PenaltyAmount\" > 0)");
            // Withdrawn: by someone, when, and why.
            t.HasCheckConstraint("CK_DisciplinaryActions_Withdrawal",
                "\"IsWithdrawn\" = (\"WithdrawnById\" IS NOT NULL AND \"WithdrawnAtUtc\" IS NOT NULL AND \"WithdrawalReason\" IS NOT NULL)");
        });
        builder.Property(a => a.SeverityLevel).IsEnumName();
        builder.Property(a => a.ViolationType).HasMaxLength(DisciplinaryAction.MaxViolationLength);
        builder.Property(a => a.Notes).HasMaxLength(DisciplinaryAction.MaxNotesLength);
        builder.Property(a => a.PenaltyAmount).HasPrecision(18, 2);
        builder.Property(a => a.IssuerId).HasMaxLength(450);
        builder.Property(a => a.IssuerName).HasMaxLength(200);
        builder.Property(a => a.WithdrawnById).HasMaxLength(450);
        builder.Property(a => a.WithdrawnBy).HasMaxLength(200);
        builder.Property(a => a.WithdrawalReason).HasMaxLength(DisciplinaryAction.MaxNotesLength);
        builder.Ignore(a => a.IsWarning);
        builder.HasOne<Employee>().WithMany().HasForeignKey(a => a.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(a => new { a.EmployeeId, a.Date });
    }
}
