using System.Globalization;
using MediatR;
using Otantik.BuildingBlocks;
using OtantikPos.Hr.Domain;
using OtantikPos.Hr.Domain.Employees;
using OtantikPos.Inventory.Application.Approvals;
using OtantikPos.Inventory.Application.Common;
using OtantikPos.Inventory.Application.RawMaterials;
using OtantikPos.Inventory.Domain.Approvals;

namespace OtantikPos.Inventory.Application.Hr;

// Employees brought in from a spreadsheet, one a row: the first data migration, or a season's
// hires. Each row is an employee as the form makes one: their name, monthly or daily pay, its
// amount, their department and role, the shift's start (or flexible hours), days off, the day
// they started and their number on the attendance device.
//
// A department not on the till is added, as the materials import adds a category; a role not in
// its department is made, as typing a new one on the form does. A row whose name, or device
// number, is already on the till (or asked for) is skipped, so a sheet imported twice adds no one
// twice; so is the same name or number again on the sheet. A manager's import adds them at once;
// anyone else's asks a manager for each, as adding one by hand does. Departments are a manager's,
// so a row of theirs naming one not on the till is skipped until a manager adds it.

// A row as read: each cell as written, the dates as yyyy-MM-dd and times as HH:mm.
public sealed record EmployeeSheetRow(
    int Row, string? Name, string? EmploymentType, string? Pay, string? Department, string? JobRole, string? ShiftStartsAt, string? DaysOff,
    string? HiredOn, string? DeviceUserId);

// One employee added, or asked of a manager (Requested).
public sealed record ImportedEmployee(int Row, Guid Id, string Name, string Department, string JobTitle, bool Requested);

public sealed record EmployeeImportResult(IReadOnlyList<ImportedEmployee> Imported, IReadOnlyList<SkippedSheetRow> Skipped, IReadOnlyList<string> DepartmentsAdded);

public sealed record ImportEmployeesCommand(IReadOnlyList<EmployeeSheetRow> Rows, string StaffId, string StaffName, bool CanApprove)
    : IRequest<EmployeeImportResult>;

internal sealed class ImportEmployeesHandler(
    EmployeeWriter writer,
    IEmployeeRepository employees,
    IDepartmentRepository departments,
    IJobRoleRepository roles,
    IApprovalRequestRepository requests,
    IUnitOfWork unitOfWork) : IRequestHandler<ImportEmployeesCommand, EmployeeImportResult>
{
    // A restaurant's staff are tens; a chain's, hundreds.
    public const int MaxRows = 1000;

    private sealed record Plan(int Row, EmployeeDetails Details, string DepartmentName, string? NewDepartmentKey);

    public async Task<EmployeeImportResult> Handle(ImportEmployeesCommand request, CancellationToken cancellationToken)
    {
        if (request.Rows.Count == 0)
            throw new DomainException("The sheet has no employees on it: one a row, under the template's headings.");
        if (request.Rows.Count > MaxRows)
            throw new DomainException($"Import at most {MaxRows:N0} employees at a time: split the sheet.");

        // Who is on the till already, those who left too (they come back, they are not added),
        // and who is asked for and not decided yet.
        var takenNames = new Dictionary<string, string>();
        var takenNumbers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in await employees.GetAllAsync(cancellationToken))
        {
            takenNames.TryAdd(SheetText.Key(e.Name), e.IsActive
                ? $"{e.Name} is on the till already."
                : $"{e.Name} is on the till already, as one who has left: bring them back on the Employees tab.");
            if (e.DeviceUserId is { } number)
                takenNumbers.TryAdd(number, $"Number {number} on the attendance device is {e.Name}'s already.");
        }

        foreach (var pending in await requests.GetPendingAsync(cancellationToken))
            if (pending is { Module: ApprovalModule.Employee, Action: ApprovalAction.Create } && ApprovalPayload.Read<EmployeeRequest>(pending).Details is { } asked)
            {
                takenNames.TryAdd(SheetText.Key(asked.Name), $"{asked.Name.Trim()} is asked for already, waiting for a manager's approval.");
                if (SheetText.Clean(asked.DeviceUserId) is { } number)
                    takenNumbers.TryAdd(number, $"Number {number} is asked for {asked.Name.Trim()} already, waiting for a manager's approval.");
            }

        var known = (await departments.GetAllAsync(cancellationToken)).OrderBy(d => d.IsGeneral ? 0 : 1).ToList();
        var byKey = new Dictionary<string, Department>();
        foreach (var d in known)
            byKey.TryAdd(SheetText.Key(d.Name), d);
        var general = known.First(d => d.IsGeneral);
        var added = new Dictionary<string, string>();

        var skipped = new List<SkippedSheetRow>();
        var plans = new List<Plan>();
        foreach (var row in request.Rows)
        {
            var name = SheetText.Clean(row.Name);
            void Skip(string reason) => skipped.Add(new SkippedSheetRow(row.Row, name, reason));

            if (name is null)
            {
                Skip("No name: every row is an employee, by their name.");
                continue;
            }

            if (takenNames.TryGetValue(SheetText.Key(name), out var whyName))
            {
                Skip(whyName);
                continue;
            }

            var number = SheetText.Clean(row.DeviceUserId is { } n ? SheetText.Western(n) : null);
            if (number is not null && takenNumbers.TryGetValue(number, out var whyNumber))
            {
                Skip(whyNumber);
                continue;
            }

            if (EmployeeCells.Type(row.EmploymentType) is not { } type)
            {
                Skip($"Employment type: \"{row.EmploymentType}\" is neither Monthly nor Daily (شهري أو يومي).");
                continue;
            }

            if (SheetText.Clean(row.Pay) is not { } payText || EmployeeCells.Money(payText) is not { } pay)
            {
                Skip(row.Pay is null
                    ? $"No base salary: their {(type == EmploymentType.Daily ? "wage a day" : "salary a month")}, as a number."
                    : $"Base salary: \"{row.Pay}\" is not a number.");
                continue;
            }

            if (SheetText.Clean(row.JobRole) is not { } title)
            {
                Skip("No job role: their role in the department, such as Chef or Waiter.");
                continue;
            }

            TimeOnly? shift = null;
            if (SheetText.Clean(row.ShiftStartsAt) is not { } shiftText)
            {
                Skip("No shift start: when their nine hours begin (10:00), or Flexible (مرن) for flexible hours.");
                continue;
            }

            if (!EmployeeCells.Flexible(shiftText))
            {
                if (EmployeeCells.Time(shiftText) is not { } start)
                {
                    Skip($"Shift start: \"{shiftText}\" is not a time of day. Write it as 10:00 or 16:30, or Flexible (مرن).");
                    continue;
                }

                shift = start;
            }

            if (!EmployeeCells.Days(row.DaysOff, out var daysOff, out var notADay))
            {
                Skip($"Days off: \"{notADay}\" is not a day of the week. Write them as Friday, Saturday or الجمعة، السبت.");
                continue;
            }

            if (SheetText.Clean(row.HiredOn) is not { } hiredText || EmployeeCells.Date(hiredText) is not { } hiredOn)
            {
                Skip(row.HiredOn is null
                    ? "No start date: the day they started, which their first month's pay counts from."
                    : $"Start date: \"{row.HiredOn}\" is not a date. Write it as 2026-10-01 or 1/10/2026.");
                continue;
            }

            // Their department, or one to add; General when none is given.
            var departmentText = SheetText.Clean(row.Department);
            var departmentKey = departmentText is null ? null : SheetText.Key(departmentText);
            Department? department = departmentKey is null ? general : byKey.GetValueOrDefault(departmentKey);
            if (department is null && !request.CanApprove)
            {
                Skip($"{departmentText} is not a department on the till: a manager adds it on the Departments tab first.");
                continue;
            }

            // Their role as the department has it, when it has one of that name.
            Guid? roleId = null;
            if (department is not null)
                foreach (var role in await roles.GetInDepartmentAsync(department.Id, cancellationToken))
                    if (SheetText.Key(role.Name) == SheetText.Key(title))
                    {
                        (roleId, title) = (role.Id, role.Name);
                        break;
                    }

            var details = new EmployeeDetails(
                name, title, type == EmploymentType.Monthly ? pay : 0, shift, daysOff, hiredOn, number, type, type == EmploymentType.Daily ? pay : 0,
                department?.Id ?? general.Id, roleId);

            // Checked as the form's would be, a department to add standing in as General.
            try
            {
                await writer.CheckAsync(Guid.NewGuid(), new EmployeeRequest(details), cancellationToken);
            }
            catch (DomainException error)
            {
                Skip(error.Message);
                continue;
            }

            if (department is null)
                added.TryAdd(departmentKey!, departmentText!);
            plans.Add(new Plan(row.Row, details, department?.Name ?? added[departmentKey!], department is null ? departmentKey : null));
            takenNames[SheetText.Key(name)] = $"{name} is on the sheet twice: row {row.Row} has them already.";
            if (number is not null)
                takenNumbers[number] = $"Number {number} is on the sheet twice: row {row.Row} has it already.";
        }

        // The departments the rows name, added first: their employees go in them.
        var made = new Dictionary<string, Guid>();
        foreach (var (key, name) in added)
        {
            var department = new Department(Guid.NewGuid(), name);
            departments.Add(department);
            made[key] = department.Id;
        }

        if (made.Count > 0)
            await unitOfWork.SaveChangesAsync(cancellationToken);

        var imported = new List<ImportedEmployee>();
        foreach (var plan in plans)
        {
            var id = Guid.NewGuid();
            var details = plan.NewDepartmentKey is { } key ? plan.Details with { DepartmentId = made[key] } : plan.Details;
            var change = new EmployeeRequest(details);
            try
            {
                if (request.CanApprove)
                {
                    await writer.ApplyAsync(id, change, cancellationToken);
                }
                else
                {
                    await writer.CheckAsync(id, change, cancellationToken);
                    var (summary, _, changes) = await writer.DescribeAsync(id, change, cancellationToken);
                    requests.Add(ApprovalRequest.AskToChange(
                        Guid.NewGuid(), ApprovalModule.Employee, ApprovalAction.Create, id, summary, changes, ApprovalPayload.Write(change),
                        "Imported from Excel", request.StaffId, request.StaffName));
                }

                await unitOfWork.SaveChangesAsync(cancellationToken);
                imported.Add(new ImportedEmployee(plan.Row, id, details.Name, plan.DepartmentName, details.JobTitle, !request.CanApprove));
            }
            catch (DomainException error)
            {
                skipped.Add(new SkippedSheetRow(plan.Row, details.Name, error.Message));
            }
        }

        return new EmployeeImportResult(imported, skipped.OrderBy(s => s.Row).ToList(), added.Values.ToList());
    }
}

// How the sheet's cells read: the words for monthly and daily pay, times, days and dates, in
// English or Arabic, Western or Arabic digits.
internal static class EmployeeCells
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static readonly Dictionary<string, DayOfWeek> DayNames = new()
    {
        ["SUN"] = DayOfWeek.Sunday, ["SUNDAY"] = DayOfWeek.Sunday, ["الاحد"] = DayOfWeek.Sunday, ["احد"] = DayOfWeek.Sunday,
        ["MON"] = DayOfWeek.Monday, ["MONDAY"] = DayOfWeek.Monday, ["الاثنين"] = DayOfWeek.Monday, ["اثنين"] = DayOfWeek.Monday,
        ["الاتنين"] = DayOfWeek.Monday, ["اتنين"] = DayOfWeek.Monday,
        ["TUE"] = DayOfWeek.Tuesday, ["TUES"] = DayOfWeek.Tuesday, ["TUESDAY"] = DayOfWeek.Tuesday, ["الثلاثاء"] = DayOfWeek.Tuesday,
        ["الثلاثا"] = DayOfWeek.Tuesday, ["ثلاثاء"] = DayOfWeek.Tuesday, ["التلات"] = DayOfWeek.Tuesday,
        ["WED"] = DayOfWeek.Wednesday, ["WEDNESDAY"] = DayOfWeek.Wednesday, ["الاربعاء"] = DayOfWeek.Wednesday, ["اربعاء"] = DayOfWeek.Wednesday,
        ["الاربع"] = DayOfWeek.Wednesday,
        ["THU"] = DayOfWeek.Thursday, ["THUR"] = DayOfWeek.Thursday, ["THURS"] = DayOfWeek.Thursday, ["THURSDAY"] = DayOfWeek.Thursday,
        ["الخميس"] = DayOfWeek.Thursday, ["خميس"] = DayOfWeek.Thursday,
        ["FRI"] = DayOfWeek.Friday, ["FRIDAY"] = DayOfWeek.Friday, ["الجمعه"] = DayOfWeek.Friday, ["جمعه"] = DayOfWeek.Friday,
        ["SAT"] = DayOfWeek.Saturday, ["SATURDAY"] = DayOfWeek.Saturday, ["السبت"] = DayOfWeek.Saturday, ["سبت"] = DayOfWeek.Saturday,
    };

    // Blank is monthly, as the form starts.
    public static EmploymentType? Type(string? text) => SheetText.Key(text ?? string.Empty) switch
    {
        "" or "MONTHLY" or "MONTH" or "SALARY" or "شهري" or "شهريه" or "عماله شهريه" => EmploymentType.Monthly,
        "DAILY" or "DAY" or "WAGE" or "يومي" or "يوميه" or "عماله يوميه" => EmploymentType.Daily,
        _ => null,
    };

    // "5,400", "5400 L.E", "٥٤٠٠ جنيه".
    public static decimal? Money(string text)
    {
        var plain = text;
        foreach (var currency in new[] { "L.E.", "L.E", "LE", "EGP", "E£", "£", "جنيه", "ج.م", "ج" })
            plain = plain.Replace(currency, string.Empty, StringComparison.OrdinalIgnoreCase);
        return SheetText.Amount(plain.Trim());
    }

    public static bool Flexible(string text) =>
        SheetText.Key(text) is "FLEXIBLE" or "FLEXIBLE HOURS" or "FLEX" or "مرن" or "مرنه" or "ساعات مرنه" or "دوام مرن";

    // "10:00", "16:30", "4 PM", "٤ م", "10", or a fraction of a day as a time cell holds one.
    public static TimeOnly? Time(string text)
    {
        var t = SheetText.Western(text).Trim();
        if (decimal.TryParse(t, NumberStyles.Number, Invariant, out var number))
        {
            if (number is >= 0 and < 1)
                return TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(Math.Round((double)number * 24 * 60) % (24 * 60)));
            if (number == Math.Floor(number) && number is >= 0 and <= 23)
                return new TimeOnly((int)number, 0);
            return null;
        }

        // The longest words first: صباحاً before صباحا before ص.
        foreach (var (word, mark) in new[]
        {
            ("صباحاً", "AM"), ("صباحا", "AM"), ("مساءً", "PM"), ("مساءا", "PM"), ("مساء", "PM"), ("ص", "AM"), ("م", "PM"), ("a.m.", "AM"), ("p.m.", "PM"),
        })
            t = t.Replace(word, $" {mark}", StringComparison.OrdinalIgnoreCase);
        t = t.Replace('.', ':');
        t = string.Join(' ', t.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        string[] formats = ["H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss", "h:mm tt", "h tt", "h:mmtt", "htt", "hh:mm tt", "h:mm:ss tt"];
        return TimeOnly.TryParseExact(t, formats, Invariant, DateTimeStyles.AllowWhiteSpaces, out var time) ? time : null;
    }

    // "Friday, Saturday", "Fri/Sat", "الجمعة والسبت"; none for blank or "None".
    public static bool Days(string? text, out IReadOnlyList<DayOfWeek> days, out string? notADay)
    {
        var found = new List<DayOfWeek>();
        days = found;
        notADay = null;
        var key = SheetText.Key(text ?? string.Empty);
        if (key is "" or "NONE" or "NO" or "-" or "لا يوجد" or "لا" or "بدون")
            return true;
        foreach (var written in text!.Split([',', '،', ';', '/', '|', '+', '&', '-', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var word = SheetText.Key(written);
            if (word is "AND" or "و")
                continue;
            if (!DayNames.TryGetValue(word, out var day) && !(word.StartsWith('و') && DayNames.TryGetValue(word[1..], out day)))
            {
                notADay = written;
                return false;
            }

            if (!found.Contains(day))
                found.Add(day);
        }

        return true;
    }

    // 2026-10-01 (a date cell), 1/10/2026 day first, or the date's serial number.
    public static DateOnly? Date(string text)
    {
        var t = SheetText.Western(text).Trim();
        if (int.TryParse(t, NumberStyles.None, Invariant, out var serial) && serial is > 20_000 and < 80_000)
            return DateOnly.FromDateTime(DateTime.FromOADate(serial));
        string[] formats = ["yyyy-MM-dd", "yyyy-M-d", "yyyy/M/d", "d/M/yyyy", "d-M-yyyy", "d.M.yyyy", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss"];
        return DateTime.TryParseExact(t, formats, Invariant, DateTimeStyles.AllowWhiteSpaces, out var date) ? DateOnly.FromDateTime(date) : null;
    }
}
