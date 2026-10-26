using Microsoft.EntityFrameworkCore;
using Otantik.BuildingBlocks;
using OtantikPos.Hr.Domain.Access;
using OtantikPos.Hr.Domain.Attendance;
using OtantikPos.Hr.Domain.Custody;
using OtantikPos.Hr.Domain.Employees;
using OtantikPos.Hr.Domain.Leaves;
using OtantikPos.Hr.Domain.Performance;
using OtantikPos.Hr.Domain.Payroll;
using OtantikPos.Inventory.Domain.Treasury;
using OtantikPos.Inventory.Domain.Waste;
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
    public async Task Custody_its_vouchers_breakage_and_the_portal_pin_are_kept_and_the_database_refuses_what_the_rules_forbid()
    {
        await using var node = await TestNode.StartAsync();
        var chef = new Employee(Guid.NewGuid(), "Hassan", "Chef", 5_400, new TimeOnly(10, 0), WeekDays.Friday, new DateOnly(2026, 1, 1));
        chef.SetMobileNumber("01012345678");
        chef.SetPinHash("AQAAAAIAAYagAAAAEH...");
        var tableware = new AssetCategory(Guid.NewGuid(), "Tableware", isConsumable: true);
        var plates = new AssetItem(Guid.NewGuid(), "Dinner plates", tableware.Id, 35, 40, AssetOrigin.HrManual, "Omar");
        var handed = plates.CheckIn(Guid.NewGuid(), chef.Id, Monday, "Kitchen", "Omar");
        var broken = plates.CheckOut(Guid.NewGuid(), AssetMovementReason.Breakage, 3, Monday, "Dropped a tray", "Omar");
        var entry = new BreakageEntry(Guid.NewGuid(), Monday, plates.Id, broken.Id, plates.Name, broken.Quantity, broken.UnitCost, broken.EmployeeId, broken.Notes, "Omar");
        broken.RouteToWaste(entry.Id);
        await node.Db(async db =>
        {
            db.Employees.Add(chef);
            db.AssetCategories.Add(tableware);
            db.AssetItems.Add(plates);
            db.AssetTransactions.AddRange(handed, broken);
            db.BreakageEntries.Add(entry);
            return await db.SaveChangesAsync();
        });

        await node.Db(async db =>
        {
            var employee = await db.Employees.SingleAsync();
            Assert.Equal(("AQAAAAIAAYagAAAAEH...", 0, true), (employee.PinHash, employee.FailedPinAttempts, employee.HasPin));
            Assert.True((await db.AssetCategories.SingleAsync()).IsConsumable);
            var item = await db.AssetItems.SingleAsync();
            Assert.Equal((37, AssetStatus.InCustody, (Guid?)chef.Id, AssetOrigin.HrManual, (decimal?)35m), (item.Quantity, item.Status, item.EmployeeId, item.Origin, item.UnitCost));
            var vouchers = await db.AssetTransactions.OrderBy(t => t.Type).ToListAsync();
            Assert.Equal(
                [(AssetTransactionType.CheckIn, 40, (AssetMovementReason?)null, (Guid?)null), (AssetTransactionType.CheckOut, 3, AssetMovementReason.Breakage, (Guid?)entry.Id)],
                vouchers.Select(t => (t.Type, t.Quantity, t.Reason, t.WasteEntryId)));
            var kept = await db.BreakageEntries.SingleAsync();
            Assert.Equal(("Dinner plates", 3, 105m, (Guid?)chef.Id, broken.Id), (kept.Description, kept.Quantity, kept.Cost, kept.EmployeeId, kept.AssetTransactionId));
            return 0;
        });

        // One employee to a mobile number: the portal signs in by it.
        await Assert.ThrowsAsync<ConflictException>(() => node.Db(async db =>
        {
            var waiter = new Employee(Guid.NewGuid(), "Mona", "Waiter", 4_000, new TimeOnly(12, 0), WeekDays.Sunday, new DateOnly(2026, 1, 1));
            waiter.SetMobileNumber("01012345678");
            db.Employees.Add(waiter);
            return await db.SaveChangesAsync();
        }));
        // Two categories of a name; a breakage for a voucher that is not there; one voucher's breakage twice.
        await Assert.ThrowsAsync<ConflictException>(() => node.Db(async db =>
        {
            db.AssetCategories.Add(new AssetCategory(Guid.NewGuid(), "Tableware", false));
            return await db.SaveChangesAsync();
        }));
        await Assert.ThrowsAsync<ConflictException>(() => node.Db(async db =>
        {
            db.BreakageEntries.Add(new BreakageEntry(Guid.NewGuid(), Monday, plates.Id, Guid.NewGuid(), "Glass", 1, 5, null, null, "Omar"));
            return await db.SaveChangesAsync();
        }));
        await Assert.ThrowsAsync<ConflictException>(() => node.Db(async db =>
        {
            db.BreakageEntries.Add(new BreakageEntry(Guid.NewGuid(), Monday, plates.Id, broken.Id, "Dinner plates", 3, 35, null, null, "Omar"));
            return await db.SaveChangesAsync();
        }));
        // The database's own checks: an asset bought on an invoice names it; a check-out says why.
        await Assert.ThrowsAnyAsync<Exception>(() => node.Db(db => db.Database.ExecuteSqlRawAsync("""
            INSERT INTO hr."AssetItems" ("Id", "Name", "Quantity", "Origin", "Status", "CreatedBy", "CreatedAtUtc")
            VALUES (gen_random_uuid(), 'Fryer', 1, 'FinancialPurchase', 'InStore', 'Omar', now())
            """)));
        await Assert.ThrowsAnyAsync<Exception>(() => node.Db(db => db.Database.ExecuteSqlAsync($"""
            INSERT INTO hr."AssetTransactions" ("Id", "AssetItemId", "Type", "Date", "Quantity", "RecordedBy", "RecordedAtUtc")
            VALUES (gen_random_uuid(), {plates.Id}, 'CheckOut', '2026-10-26', 1, 'Omar', now())
            """)));
    }

    [Fact]
    public async Task Leave_types_balances_and_requests_are_kept_and_the_database_refuses_what_the_rules_forbid()
    {
        await using var node = await TestNode.StartAsync();
        var chef = new Employee(Guid.NewGuid(), "Hassan", "Chef", 5_400, new TimeOnly(10, 0), WeekDays.Friday, new DateOnly(2026, 1, 1));
        var annual = new LeaveType(Guid.NewGuid(), "Annual", isPaid: true, defaultDaysPerYear: 21);
        var unpaid = new LeaveType(Guid.NewGuid(), "Unpaid", isPaid: false, defaultDaysPerYear: 0);
        var balance = EmployeeLeaveBalance.Open(chef, annual, 2026);
        balance.Use(4);
        var approved = new LeaveRequest(Guid.NewGuid(), chef.Id, annual.Id, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 5), 4, "Wedding", "Hassan");
        approved.Approve("staff-omar", "Omar", "Enjoy it");
        var waiting = new LeaveRequest(Guid.NewGuid(), chef.Id, unpaid.Id, new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 1), 1, null, "Hassan");
        await node.Db(async db =>
        {
            db.Employees.Add(chef);
            db.LeaveTypes.AddRange(annual, unpaid);
            db.EmployeeLeaveBalances.Add(balance);
            db.LeaveRequests.AddRange(approved, waiting);
            return await db.SaveChangesAsync();
        });

        using (var scope = node.Scope())
        {
            var kept = await node.Service<OtantikPos.Hr.Domain.IEmployeeLeaveBalanceRepository>(scope).GetAsync(chef.Id, annual.Id, 2026);
            Assert.Equal((21m, 4m, 17m, 2026), (kept!.TotalAllocatedDays, kept.UsedDays, kept.RemainingDays, kept.Year));
            var requests = node.Service<OtantikPos.Hr.Domain.ILeaveRequestRepository>(scope);
            Assert.Equal([waiting.Id], (await requests.GetPendingAsync()).Select(r => r.Id));
            Assert.Equal([waiting.Id, approved.Id], (await requests.GetForEmployeeAsync(chef.Id)).Select(r => r.Id));
            var read = (await requests.GetForEmployeeAsync(chef.Id)).Single(r => r.Id == approved.Id);
            Assert.Equal((LeaveRequestStatus.Approved, "staff-omar", "Enjoy it", 4), (read.Status, read.DecidedById, read.ManagerNotes, read.TotalDaysRequested));
            // A day asked for once: what meets those dates, waiting or approved.
            Assert.Equal([approved.Id], (await requests.GetOverlappingAsync(chef.Id, new DateOnly(2026, 11, 5), new DateOnly(2026, 11, 9))).Select(r => r.Id));
            Assert.Empty(await requests.GetOverlappingAsync(chef.Id, new DateOnly(2026, 11, 6), new DateOnly(2026, 11, 9)));
            Assert.True(await requests.AnyForTypeAsync(unpaid.Id));
            Assert.True(await node.Service<OtantikPos.Hr.Domain.IEmployeeLeaveBalanceRepository>(scope).AnyForTypeAsync(annual.Id));
            // Beside the three the till starts with.
            Assert.Equal(["Annual", "Annual (اعتيادي)", "Sick (مرضي)", "Unpaid", "Unpaid (بدون راتب)"],
                (await node.Service<OtantikPos.Hr.Domain.ILeaveTypeRepository>(scope).GetAllAsync()).Select(t => t.Name).Order(StringComparer.Ordinal));
        }

        // Two types of a name; two balances of a type in a year.
        await Assert.ThrowsAsync<ConflictException>(() => node.Db(async db =>
        {
            db.LeaveTypes.Add(new LeaveType(Guid.NewGuid(), "Annual", true, 30));
            return await db.SaveChangesAsync();
        }));
        await Assert.ThrowsAsync<ConflictException>(() => node.Db(async db =>
        {
            db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance(chef.Id, annual.Id, 2026, 30, "Omar"));
            return await db.SaveChangesAsync();
        }));
        // The database's own checks: more taken than given; unpaid leave with days a year; a leave
        // across a year's end; one decided by no one.
        await Assert.ThrowsAnyAsync<Exception>(() => node.Db(db => db.Database.ExecuteSqlAsync($"""
            UPDATE hr."EmployeeLeaveBalances" SET "UsedDays" = 22 WHERE "Id" = {balance.Id}
            """)));
        await Assert.ThrowsAnyAsync<Exception>(() => node.Db(db => db.Database.ExecuteSqlAsync($"""
            UPDATE hr."LeaveTypes" SET "DefaultDaysPerYear" = 5 WHERE "Id" = {unpaid.Id}
            """)));
        await Assert.ThrowsAnyAsync<Exception>(() => node.Db(db => db.Database.ExecuteSqlAsync($"""
            UPDATE hr."LeaveRequests" SET "EndDate" = '2027-01-02' WHERE "Id" = {waiting.Id}
            """)));
        await Assert.ThrowsAnyAsync<Exception>(() => node.Db(db => db.Database.ExecuteSqlAsync($"""
            UPDATE hr."LeaveRequests" SET "Status" = 'Approved' WHERE "Id" = {waiting.Id}
            """)));
    }

    [Fact]
    public async Task Evaluations_and_disciplinary_actions_are_kept_and_a_penalty_deduction_is_its_own_beside_a_managers()
    {
        await using var node = await TestNode.StartAsync();
        var chef = new Employee(Guid.NewGuid(), "Hassan", "Chef", 5_400, new TimeOnly(10, 0), WeekDays.Friday, new DateOnly(2026, 1, 1));
        var punctuality = new PerformanceKpi(Guid.NewGuid(), "Punctuality");
        var hygiene = new PerformanceKpi(Guid.NewGuid(), "Hygiene");
        var evaluation = new PerformanceEvaluation(Guid.NewGuid(), chef.Id, Monday, [(punctuality, 5), (hygiene, 3)], "Good month", "staff-omar", "Omar");
        var warning = new DisciplinaryAction(Guid.NewGuid(), chef.Id, Monday, "Negligence", SeverityLevel.FirstWritten, null, null, "staff-omar", "Omar");
        var deduction = new DisciplinaryAction(Guid.NewGuid(), chef.Id, Monday, "Absence", SeverityLevel.PenaltyDeduction, "Third time", 250, "staff-omar", "Omar");
        // The manager's own hours on the same day, and the deduction's penalty beside them.
        var own = new AdministrativePenalty(chef.Id, Monday, 2, "Late again", "Omar");
        var day = new DayEvaluation(Monday, DayStatus.Worked, null, null, 2, 540, 0, 0, 0, 0, PenaltyHours: 2, PenaltyAmount: 250);
        await node.Db(async db =>
        {
            db.Employees.Add(chef);
            db.PerformanceKpis.AddRange(punctuality, hygiene);
            db.PerformanceEvaluations.Add(evaluation);
            db.DisciplinaryActions.AddRange(warning, deduction);
            db.AdministrativePenalties.AddRange(own, deduction.Penalty()!);
            db.DailyRecords.Add(new DailyRecord(chef.Id, day));
            return await db.SaveChangesAsync();
        });

        using (var scope = node.Scope())
        {
            var kept = (await node.Service<OtantikPos.Hr.Domain.IPerformanceEvaluationRepository>(scope).GetForEmployeeAsync(chef.Id)).Single();
            Assert.Equal((80.0m, "Omar", "Good month"), (kept.TotalScore, kept.ReviewerName, kept.Notes));
            Assert.Equal([("Punctuality", 5), ("Hygiene", 3)], kept.Scores.OrderByDescending(s => s.Score).Select(s => (s.KpiName, s.Score)));
            Assert.True(await node.Service<OtantikPos.Hr.Domain.IPerformanceEvaluationRepository>(scope).AnyForKpiAsync(hygiene.Id));
            Assert.Equal(["Hygiene", "Punctuality"], (await node.Service<OtantikPos.Hr.Domain.IPerformanceKpiRepository>(scope).GetAllAsync()).Select(k => k.Name));

            var actions = await node.Service<OtantikPos.Hr.Domain.IDisciplinaryActionRepository>(scope).GetForEmployeeAsync(chef.Id);
            Assert.Equal((1, 1, SeverityLevel.PenaltyDeduction), (DisciplinaryRecord.Of(actions).FirstWritten, DisciplinaryRecord.Of(actions).PenaltyDeductions, DisciplinaryRecord.Of(actions).Next));
            Assert.Equal((decimal?)250m, actions.Single(a => a.Id == deduction.Id).PenaltyAmount);

            var penalties = node.Service<OtantikPos.Hr.Domain.IAdministrativePenaltyRepository>(scope);
            Assert.Equal(own.Id, (await penalties.GetAsync(chef.Id, Monday))!.Id);
            Assert.Equal((decimal?)250m, (await penalties.GetForDisciplinaryActionAsync(deduction.Id))!.Amount);
            Assert.Equal(2, (await penalties.GetBetweenAsync(Monday, Monday)).Count);
        }

        Assert.Equal((2m, 250m), await node.Db(async db =>
        {
            var record = await db.DailyRecords.SingleAsync();
            return (record.AdministrativePenaltyHours, record.AdministrativePenaltyAmount);
        }));

        // Two KPIs of a name; a second penalty for the same deduction; a manager's own twice on a day.
        await Assert.ThrowsAsync<ConflictException>(() => node.Db(async db =>
        {
            db.PerformanceKpis.Add(new PerformanceKpi(Guid.NewGuid(), "Hygiene"));
            return await db.SaveChangesAsync();
        }));
        await Assert.ThrowsAsync<ConflictException>(() => node.Db(async db =>
        {
            db.AdministrativePenalties.Add(AdministrativePenalty.ForAmount(chef.Id, Monday.AddDays(1), 100, "Again", "Omar", deduction.Id));
            return await db.SaveChangesAsync();
        }));
        await Assert.ThrowsAsync<ConflictException>(() => node.Db(async db =>
        {
            db.AdministrativePenalties.Add(AdministrativePenalty.ForAmount(chef.Id, Monday, 100, "Again", "Omar"));
            return await db.SaveChangesAsync();
        }));
        // The database's own checks: hours and an amount at once; a warning with an amount; a score past 5.
        await Assert.ThrowsAnyAsync<Exception>(() => node.Db(db => db.Database.ExecuteSqlAsync($"""
            UPDATE hr."AdministrativePenalties" SET "Amount" = 10 WHERE "Id" = {own.Id}
            """)));
        await Assert.ThrowsAnyAsync<Exception>(() => node.Db(db => db.Database.ExecuteSqlAsync($"""
            UPDATE hr."DisciplinaryActions" SET "PenaltyAmount" = 10 WHERE "Id" = {warning.Id}
            """)));
        await Assert.ThrowsAnyAsync<Exception>(() => node.Db(db => db.Database.ExecuteSqlAsync($"""
            UPDATE hr."EvaluationScores" SET "Score" = 6 WHERE "KpiId" = {hygiene.Id}
            """)));
    }

    [Fact]
    public async Task HR_tabs_are_kept_by_name_and_an_accountant_has_one_row()
    {
        await using var node = await TestNode.StartAsync();
        var supervisor = new Employee(Guid.NewGuid(), "Hassan", "Supervisor", 9_000, new TimeOnly(10, 0), WeekDays.Friday, new DateOnly(2026, 1, 1));
        var cook = new Employee(Guid.NewGuid(), "Mona", "Cook", 5_400, new TimeOnly(10, 0), WeekDays.Friday, new DateOnly(2026, 1, 1));
        supervisor.SetHrTabs(HrTabs.Now | HrTabs.Attendance);
        await node.Db(async db =>
        {
            db.Employees.AddRange(supervisor, cook);
            db.StaffHrAccess.Add(new StaffHrAccess("staff-accountant", HrTabs.Payroll | HrTabs.Advances, "Omar"));
            return await db.SaveChangesAsync();
        });

        await node.Db(async db =>
        {
            Assert.Equal(HrTabs.Now | HrTabs.Attendance, (await db.Employees.SingleAsync(e => e.Id == supervisor.Id)).HrTabs);
            Assert.Equal(HrTabs.None, (await db.Employees.SingleAsync(e => e.Id == cook.Id)).HrTabs);
            var mona = await db.StaffHrAccess.SingleAsync(a => a.StaffId == "staff-accountant");
            Assert.Equal((HrTabs.Payroll | HrTabs.Advances, "Omar"), (mona.Tabs, mona.SetBy));
            // As they are written: by name.
            Assert.Equal(["Now, Attendance"], await db.Database.SqlQuery<string>($"SELECT \"HrTabs\" AS \"Value\" FROM hr.\"Employees\" WHERE \"Id\" = {supervisor.Id}").ToListAsync());
            return 0;
        });

        // One row to an account.
        await Assert.ThrowsAsync<ConflictException>(() => node.Db(async db =>
        {
            db.StaffHrAccess.Add(new StaffHrAccess("staff-accountant", HrTabs.Now, "Sara"));
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
