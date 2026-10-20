using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OtantikPos.Hr.Domain.Attendance;
using OtantikPos.Node.Infrastructure.Persistence;

namespace OtantikPos.Node.Infrastructure.Hr;

// How the attendance device's punches are read (إعدادات الحضور): one row, set by a manager. None
// set yet, no grace for arriving late, and scans within three minutes of each other are one.
public sealed class HrSettings
{
    public const int SingletonId = 1;

    public int Id { get; init; } = SingletonId;
    public int GraceMinutes { get; set; } = AttendancePolicy.Default.GraceMinutes;
    public int DoubleScanMinutes { get; set; } = AttendancePolicy.Default.DoubleScanMinutes;

    public AttendancePolicy ToPolicy() => new(GraceMinutes, DoubleScanMinutes);
}

internal sealed class HrSettingsConfiguration : IEntityTypeConfiguration<HrSettings>
{
    public void Configure(EntityTypeBuilder<HrSettings> builder)
    {
        builder.ToTable("Settings", NodeDbContext.HrSchema);
        builder.Property(s => s.Id).ValueGeneratedNever();
    }
}
