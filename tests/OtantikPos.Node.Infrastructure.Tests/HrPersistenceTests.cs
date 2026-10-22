using Microsoft.EntityFrameworkCore;
using Otantik.BuildingBlocks;
using OtantikPos.Hr.Domain.Attendance;
using OtantikPos.Hr.Domain.Employees;
using OtantikPos.Hr.Domain.Payroll;
using OtantikPos.Inventory.Domain.Treasury;
using OtantikPos.Node.Infrastructure.Hr;

namespace OtantikPos.Node.Infrastructure.Tests;

// The hr schema against PostgreSQL: each record kept and read back as it was, punches on the wall
// clock, and the database refusing what the rules forbid twice over.
public class HrPersistenceTests
{
    private const string Serial = "CQZ7232060112";
    private static readonly DateOnly October = new(2026, 10, 1);
    private static readonly DateOnly Monday = new(2026, 10, 12);
    private static readonly DateTime PunchIn = new(2026, 10, 12, 10, 4, 0);

    [Fact]
    public async Task Employees_their_punches_days_and_pay_are_kept_as_they_were()
    {
        await using var node = await TestNode.StartAsync();
        var chef = new Employee(Guid.NewGuid(), "حسن", "Chef", 5_400, new TimeOnly(10, 0), WeekDays.Friday | WeekDays.Saturday, new DateOnly(2026, 1, 1), "7");
        var day = Timesheet.Evaluate(chef, Monday, [PunchIn, PunchIn.AddHours(9)], null, AttendancePolicy.Default, new DateTime(2026, 10, 20)) with { PenaltyHours = 1.5m };
        // Armed Forces Day, Tuesday 6 October, worked.
        var armedForcesDay = new DateOnly(2026, 10, 6);
        var holiday = Timesheet.Evaluate(
            chef, armedForcesDay, [armedForcesDay.ToDateTime(new TimeOnly(10, 0)), armedForcesDay.ToDateTime(new TimeOnly(19, 0))], null,
            AttendancePolicy.Default, new DateTime(2026, 10, 20), holiday: true);
        var incentive = new PayrollAdjustment(Guid.NewGuid(), chef.Id, October, AdjustmentKind.Incentive, 500, "Best month", "Omar");
        var figures = PayrollCalculator.Calculate(chef, October, [day, holiday], [incentive], advances: 300, broughtForward: 0);
        await node.Db(async db =>
        {
            db.Employees.Add(chef);
            var device = AttendanceDevice.FirstSeen(Serial, DateTime.UtcNow, "192.168.1.40");
            device.Approve("Kitchen door", "Omar");
            device.Acknowledge("9999");
            db.AttendanceDevices.Add(device);
            db.AttendanceLogs.Add(AttendanceLog.FromDevice(Serial, "7", PunchIn, 0, 1, chef.Id));
            db.AttendanceLogs.Add(AttendanceLog.FromDevice(Serial, "99", PunchIn, 0, 15, null));
            db.DayMarks.Add(new DayMark(chef.Id, Monday.AddDays(1), DayMarkKind.Leave, "Wedding", "Omar"));
            db.AdministrativePenalties.Add(new AdministrativePenalty(chef.Id, Monday, 1.5m, "Left the kitchen open", "Omar"));
            db.DailyRecords.Add(new DailyRecord(chef.Id, day));
            db.DailyRecords.Add(new DailyRecord(chef.Id, holiday));
            db.PublicHolidays.Add(new PublicHoliday(Guid.NewGuid(), "Armed Forces Day", armedForcesDay, armedForcesDay, "Omar"));
            db.PayrollAdjustments.Add(incentive);
            db.SafeTransactions.Add(SafeTransaction.EmployeeAdvance(Guid.NewGuid(), 300, Monday, chef.Id, October, null, "Omar", revenueDate: Monday));
            db.PayrollSlips.Add(new PayrollSlip(Guid.NewGuid(), chef.Id, October, figures));
            db.HrSettings.Add(new HrSettings { GraceMinutes = 10 });
            return await db.SaveChangesAsync();
        });

        await node.Db(async db =>
        {
            var employee = await db.Employees.SingleAsync();
            Assert.Equal(("حسن", WeekDays.Friday | WeekDays.Saturday, "7", new TimeOnly(10, 0)), (employee.Name, employee.DaysOff, employee.DeviceUserId, employee.ShiftStartsAt));

            var logs = await db.AttendanceLogs.OrderBy(l => l.DeviceUserId).ToListAsync();
            Assert.Equal([("7", (Guid?)chef.Id), ("99", null)], logs.Select(l => (l.DeviceUserId!, l.EmployeeId)));
            // The device's wall clock, as it was: no time zone to shift it.
            Assert.Equal((PunchIn, DateTimeKind.Unspecified), (logs[0].PunchedAt, logs[0].PunchedAt.Kind));

            var device = await db.AttendanceDevices.SingleAsync();
            Assert.Equal(("Kitchen door", true, "9999"), (device.Name, device.IsApproved, device.AttendanceStamp));

            // Four minutes late with no grace, made up at the end: no overtime, as nine hours were not
            // passed. The day's administrative penalty kept with it.
            var record = await db.DailyRecords.SingleAsync(r => r.Date == Monday);
            Assert.Equal(day, record.ToEvaluation());
            Assert.Equal(holiday, (await db.DailyRecords.SingleAsync(r => r.Date == armedForcesDay)).ToEvaluation());
            var kept = await db.PublicHolidays.SingleAsync();
            Assert.Equal(("Armed Forces Day", armedForcesDay, armedForcesDay, 1), (kept.Name, kept.FirstDay, kept.LastDay, kept.Days));
            Assert.Equal((DayStatus.Worked, 540, 4, 0, 1.5m), (record.Status, record.WorkedMinutes, record.LateMinutes, record.OvertimeMinutes, record.AdministrativePenaltyHours));
            var penalty = await db.AdministrativePenalties.SingleAsync();
            Assert.Equal((1.5m, "Left the kitchen open"), (penalty.Hours, penalty.Reason));

            Assert.Equal(DayMarkKind.Leave, (await db.DayMarks.SingleAsync()).Kind);

            var slip = await db.PayrollSlips.SingleAsync();
            Assert.Equal(figures, slip.Figures);
            Assert.Equal((DayStatus.WorkedOnHoliday, 1, 180m), (holiday.Status, slip.Figures.HolidaysWorked, slip.Figures.HolidaysWorkedPay));
            Assert.Equal((PayrollSlipStatus.Draft, figures.NetPayable, figures.WagesCost), (slip.Status, slip.NetPayable, slip.WagesCost));

            var advance = await db.SafeTransactions.SingleAsync(t => t.Kind == SafeTransactionKind.EmployeeAdvance);
            Assert.Equal((CashFund.DayRevenue, -300m, (Guid?)chef.Id, (DateOnly?)October), (advance.Fund, advance.Amount, advance.EmployeeId, advance.PayrollMonth));
            Assert.Equal(new AttendancePolicy(10, 3), (await db.HrSettings.SingleAsync()).ToPolicy());
            return 0;
        });
    }

    [Fact]
    public async Task Flexible_hours_device_commands_and_users_and_a_settled_late_punch_are_kept()
    {
        await using var node = await TestNode.StartAsync();
        var waiter = new Employee(Guid.NewGuid(), "Mona", "Waiter", 4_050, null, WeekDays.Friday, new DateOnly(2026, 1, 1), "12");
        var service = new Department(Guid.NewGuid(), "Service");
        waiter.SetPay(EmploymentType.Daily, 250);
        waiter.MoveTo(service.Id);
        var punch = AttendanceLog.FromDevice(Serial, "12", PunchIn, 0, 1, waiter.Id);
        punch.SettleLate("Omar");
        var command = DeviceCommand.ReadUsers(Serial, 1, "Omar");
        command.Sent(DateTime.UtcNow);
        command.Answered(0, DateTime.UtcNow);
        await node.Db(async db =>
        {
            db.Departments.Add(service);
            db.Employees.Add(waiter);
            db.AttendanceLogs.Add(punch);
            db.DeviceCommands.Add(command);
            db.DeviceUsers.Add(new DeviceUser(Serial, "12", "منى", DateTime.UtcNow));
            return await db.SaveChangesAsync();
        });

        await node.Db(async db =>
        {
            var kept = (await db.Employees.SingleAsync());
            Assert.Equal(((TimeOnly?)null, EmploymentType.Daily, 250m, 0m, service.Id), (kept.ShiftStartsAt, kept.EmploymentType, kept.DailyWage, kept.MonthlySalary, kept.DepartmentId));
            // General is made with the table.
            Assert.Equal(["General", "Service"], await db.Departments.OrderBy(d => d.Name).Select(d => d.Name).ToListAsync());
            Assert.Equal("Omar", (await db.AttendanceLogs.SingleAsync()).LateSettledBy);
            var command = await db.DeviceCommands.SingleAsync();
            Assert.Equal((DeviceCommandKind.ReadUsers, "C:1:DATA QUERY USERINFO", (int?)0), (command.Kind, command.Line, command.ReturnCode));
            Assert.Equal(("12", "منى"), ((await db.DeviceUsers.SingleAsync()).DeviceUserId, (await db.DeviceUsers.SingleAsync()).Name));
            return 0;
        });

        // A device's command numbers, and its users, are one each.
        await Assert.ThrowsAsync<ConflictException>(() => node.Db(async db =>
        {
            db.DeviceCommands.Add(DeviceCommand.ReadUsers(Serial, 1, "Omar"));
            return await db.SaveChangesAsync();
        }));
        await Assert.ThrowsAsync<ConflictException>(() => node.Db(async db =>
        {
            db.DeviceUsers.Add(new DeviceUser(Serial, "12", "Mona", DateTime.UtcNow));
            return await db.SaveChangesAsync();
        }));
    }

    [Fact]
    public async Task An_employees_id_card_and_how_to_reach_them_are_kept_and_a_card_is_one_employees()
    {
        await using var node = await TestNode.StartAsync();
        var chef = new Employee(Guid.NewGuid(), "حسن محمود علي", "Chef", 5_400, new TimeOnly(10, 0), WeekDays.Friday, new DateOnly(2026, 1, 1));
        chef.SetNationalId("29001010123456");
        chef.SetMobileNumber("01012345678");
        chef.AttachIdPhoto("card.png", "image/png", 4, "Omar");
        byte[] png = [0x89, 0x50, 0x4E, 0x47];
        await node.Db(async db =>
        {
            db.Employees.Add(chef);
            db.EmployeeIdPhotos.Add(new EmployeeIdPhoto { EmployeeId = chef.Id, Content = png });
            return await db.SaveChangesAsync();
        });

        await node.Db(async db =>
        {
            var kept = await db.Employees.SingleAsync();
            Assert.Equal(("29001010123456", "01012345678", "card.png", "image/png", (int?)4, "Omar"),
                (kept.NationalId, kept.MobileNumber, kept.IdPhotoFileName, kept.IdPhotoContentType, kept.IdPhotoBytes, kept.IdPhotoUploadedBy));
            Assert.True(kept.HasIdPhoto);
            Assert.Equal(png, (await db.EmployeeIdPhotos.SingleAsync()).Content);
            return 0;
        });

        // Two employees with one card is a mistake the database refuses too.
        await Assert.ThrowsAsync<ConflictException>(() => node.Db(async db =>
        {
            var copy = new Employee(Guid.NewGuid(), "Karim", "Waiter", 4_000, new TimeOnly(12, 0), WeekDays.Sunday, new DateOnly(2026, 1, 1));
            copy.SetNationalId("29001010123456");
            db.Employees.Add(copy);
            return await db.SaveChangesAsync();
        }));
        // Nor a photo for no one.
        await Assert.ThrowsAsync<ConflictException>(() => node.Db(async db =>
        {
            db.EmployeeIdPhotos.Add(new EmployeeIdPhoto { EmployeeId = Guid.NewGuid(), Content = png });
            return await db.SaveChangesAsync();
        }));
    }

    [Fact]
    public async Task A_punch_sent_twice_is_kept_once_and_a_device_number_is_one_employees()
    {
        await using var node = await TestNode.StartAsync();
        var chef = new Employee(Guid.NewGuid(), "Hassan", "Chef", 5_400, new TimeOnly(10, 0), WeekDays.Friday, new DateOnly(2026, 1, 1), "7");
        await node.Db(async db =>
        {
            db.Employees.Add(chef);
            db.AttendanceLogs.Add(AttendanceLog.FromDevice(Serial, "7", PunchIn, 0, 1, chef.Id));
            return await db.SaveChangesAsync();
        });

        await Assert.ThrowsAsync<ConflictException>(() => node.Db(async db =>
        {
            db.AttendanceLogs.Add(AttendanceLog.FromDevice(Serial, "7", PunchIn, 1, 1, chef.Id));
            return await db.SaveChangesAsync();
        }));
        // The same moment from another device, or entered by hand, is another punch.
        await node.Db(async db =>
        {
            db.AttendanceLogs.Add(AttendanceLog.FromDevice("CQZ7232060999", "7", PunchIn, 0, 1, chef.Id));
            db.AttendanceLogs.Add(AttendanceLog.Manual(Guid.NewGuid(), chef.Id, PunchIn, "Forgot to scan", "Omar"));
            return await db.SaveChangesAsync();
        });
        Assert.Equal(3, await node.Db(db => db.AttendanceLogs.CountAsync()));

        await Assert.ThrowsAsync<ConflictException>(() => node.Db(async db =>
        {
            db.Employees.Add(new Employee(Guid.NewGuid(), "Karim", "Waiter", 4_000, new TimeOnly(12, 0), WeekDays.Sunday, new DateOnly(2026, 1, 1), "7"));
            return await db.SaveChangesAsync();
        }));
        // An advance names someone on the payroll.
        await Assert.ThrowsAsync<ConflictException>(() => node.Db(async db =>
        {
            db.SafeTransactions.Add(SafeTransaction.EmployeeAdvance(Guid.NewGuid(), 300, Monday, Guid.NewGuid(), October, null, "Omar"));
            return await db.SaveChangesAsync();
        }));
    }
}
