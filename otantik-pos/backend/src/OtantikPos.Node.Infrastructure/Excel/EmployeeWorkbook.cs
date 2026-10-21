using System.Globalization;
using ClosedXML.Excel;
using Otantik.BuildingBlocks;
using OtantikPos.Hr.Domain.Employees;
using OtantikPos.Inventory.Application.Hr;

namespace OtantikPos.Node.Infrastructure.Excel;

// The employees spreadsheet: the template to fill in, reading one back for the import
// (ImportEmployeesCommand, which decides what each row becomes), and the employees as they are,
// exported in the template's columns, so an export can be added to and imported again.
//
// Columns are found by their heading, in English or Arabic, not by position.
public static class EmployeeWorkbook
{
    public const string TemplateFileName = "employees-template.xlsx";
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string DataSheetName = "Employees";
    private const string HelpSheetName = "How to fill";
    private const string ListsSheetName = "Lists";
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public const string NameHeader = "Name · الاسم";
    public const string TypeHeader = "Employment Type · نوع العمالة";
    public const string PayHeader = "Base Salary · الراتب الأساسي";
    public const string DepartmentHeader = "Department · القسم";
    public const string RoleHeader = "Job Role · الوظيفة";
    public const string ShiftHeader = "Shift Start · بداية الوردية";
    public const string DaysOffHeader = "Days Off · أيام الراحة";
    public const string HiredOnHeader = "Hired On · تاريخ التعيين";
    public const string DeviceHeader = "Device No. · رقم البصمة";
    private const string DayRateHeader = "Day Rate · أجر اليوم";
    private const string HourRateHeader = "Hour Rate · أجر الساعة";

    private static readonly string[] Headers = [NameHeader, TypeHeader, PayHeader, DepartmentHeader, RoleHeader, ShiftHeader, DaysOffHeader, HiredOnHeader, DeviceHeader];
    private static readonly double[] Widths = [30, 22, 22, 22, 22, 22, 26, 22, 20];

    // How each column may be headed. A heading matches when any part of it, split at "·" or a
    // bracket, is one of these, capitals and spacing aside.
    private static readonly string[] NameNames = ["name", "employee", "employee name", "full name", "الاسم", "اسم الموظف", "الموظف"];
    private static readonly string[] TypeNames = ["employment type", "type", "pay type", "نوع العمالة", "نوع التعاقد", "العمالة"];
    private static readonly string[] PayNames = ["base salary", "salary", "pay", "wage", "monthly salary", "daily wage", "الراتب الأساسي", "الراتب", "الأجر", "الاجر", "اليومية"];
    private static readonly string[] DepartmentNames = ["department", "dept", "القسم", "الإدارة", "الادارة"];
    private static readonly string[] RoleNames = ["job role", "role", "job title", "job", "title", "position", "الوظيفة", "المسمى الوظيفي"];
    private static readonly string[] ShiftNames = ["shift start", "shift", "starts at", "start time", "بداية الوردية", "الوردية", "موعد الحضور"];
    private static readonly string[] DaysOffNames = ["days off", "day off", "weekly off", "rest days", "أيام الراحة", "ايام الراحة", "يوم الراحة", "الراحة", "الإجازة الأسبوعية"];
    private static readonly string[] HiredOnNames = ["hired on", "hire date", "start date", "date hired", "joined", "تاريخ التعيين", "تاريخ الالتحاق", "بداية العمل"];
    private static readonly string[] DeviceNames = ["device no.", "device no", "device number", "device", "attendance number", "fingerprint", "رقم البصمة", "البصمة", "رقم الجهاز"];

    // Rows the dropdowns and checks cover, past which a sheet still imports without them.
    private const int FormattedRows = 1000;

    // Most a sheet may hold, before anything is read into rows.
    private const int MaxRows = 5000;

    // The template: the columns, with dropdowns for monthly or daily pay, the department and the
    // role (the restaurant's own, a new name allowed), a sheet of how to fill it with examples,
    // and a hidden sheet of the lists. No example rows on the sheet itself: left in, they would be
    // imported.
    public static Stream Template(IReadOnlyList<DepartmentDto> departments)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(DataSheetName);
        Heading(sheet);
        sheet.Cell(1, 3).CreateComment().AddText("A month's salary for monthly pay, a day's wage for daily pay.");
        sheet.Cell(1, 6).CreateComment().AddText("When their nine hours begin: 10:00, 16:30. Flexible (مرن) for flexible hours.");
        sheet.Cell(1, 7).CreateComment().AddText("Friday, Saturday or الجمعة، السبت. Blank for none.");
        sheet.Cell(1, 8).CreateComment().AddText("The day they started: their first month's pay counts from it.");
        sheet.Cell(1, 9).CreateComment().AddText("Their user number on the attendance device. Optional.");
        Formats(sheet.Range(2, 1, FormattedRows + 1, Headers.Length));

        var lists = workbook.Worksheets.Add(ListsSheetName);
        lists.Cell(1, 1).Value = "Types";
        lists.Cell(1, 2).Value = "Departments";
        lists.Cell(1, 3).Value = "Roles";
        string[] types = ["Monthly", "Daily", "شهري", "يومي"];
        for (var i = 0; i < types.Length; i++)
            lists.Cell(i + 2, 1).Value = types[i];
        var names = departments.OrderBy(d => d.IsGeneral ? 0 : 1).ThenBy(d => d.Name).Select(d => d.Name).ToList();
        for (var i = 0; i < names.Count; i++)
            lists.Cell(i + 2, 2).Value = names[i];
        var roles = departments.SelectMany(d => d.Roles).Select(r => r.Name).Distinct(StringComparer.CurrentCultureIgnoreCase).Order().ToList();
        for (var i = 0; i < roles.Count; i++)
            lists.Cell(i + 2, 3).Value = roles[i];
        lists.Visibility = XLWorksheetVisibility.Hidden;

        var type = sheet.Range(2, 2, FormattedRows + 1, 2).CreateDataValidation();
        type.List($"'{ListsSheetName}'!$A$2:$A${types.Length + 1}", true);
        type.IgnoreBlanks = true;
        type.ErrorStyle = XLErrorStyle.Warning;
        type.ErrorTitle = "Monthly or Daily";
        type.ErrorMessage = "The till takes Monthly or Daily (شهري أو يومي). Blank: Monthly.";

        var pay = sheet.Range(2, 3, FormattedRows + 1, 3).CreateDataValidation();
        pay.Decimal.EqualOrGreaterThan(0);
        pay.IgnoreBlanks = true;
        pay.ErrorStyle = XLErrorStyle.Stop;
        pay.ErrorTitle = "A salary";
        pay.ErrorMessage = "Write the salary as a number, 0 or more: a month's, or a day's for daily pay.";

        // A department or role not on the list is allowed: it is added on import.
        var department = sheet.Range(2, 4, FormattedRows + 1, 4).CreateDataValidation();
        department.List($"'{ListsSheetName}'!$B$2:$B${Math.Max(names.Count, 1) + 1}", true);
        department.IgnoreBlanks = true;
        department.ErrorStyle = XLErrorStyle.Information;
        department.ErrorTitle = "A new department";
        department.ErrorMessage = "Not on the till yet: a manager's import adds it. Leave it blank for General.";

        var role = sheet.Range(2, 5, FormattedRows + 1, 5).CreateDataValidation();
        role.List($"'{ListsSheetName}'!$C$2:$C${Math.Max(roles.Count, 1) + 1}", true);
        role.IgnoreBlanks = true;
        role.ErrorStyle = XLErrorStyle.Information;
        role.ErrorTitle = "A new role";
        role.ErrorMessage = "Not in that department yet: it is made on import.";

        AddHelp(workbook);
        sheet.SetTabActive();
        return Saved(workbook);
    }

    // The employees as they are, in the template's columns and two more: what a day and an hour
    // of theirs are paid. A styled table, filtered by any column.
    public static Stream Export(IReadOnlyList<EmployeeDto> employees, IReadOnlyList<DepartmentDto> departments)
    {
        var names = departments.ToDictionary(d => d.Id, d => d.Name);
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(DataSheetName);
        string[] headers = [.. Headers, DayRateHeader, HourRateHeader];
        for (var i = 0; i < headers.Length; i++)
            sheet.Cell(1, i + 1).Value = headers[i];

        var row = 2;
        foreach (var e in employees.OrderBy(e => names.GetValueOrDefault(e.DepartmentId)).ThenBy(e => e.Name))
        {
            sheet.Cell(row, 1).Value = e.Name;
            sheet.Cell(row, 2).Value = e.EmploymentType == EmploymentType.Daily ? "Daily" : "Monthly";
            sheet.Cell(row, 3).Value = e.EmploymentType == EmploymentType.Daily ? e.DailyWage : e.MonthlySalary;
            sheet.Cell(row, 4).Value = names.GetValueOrDefault(e.DepartmentId) ?? string.Empty;
            sheet.Cell(row, 5).Value = e.JobTitle;
            if (e.ShiftStartsAt is { } start)
                sheet.Cell(row, 6).Value = start.ToTimeSpan();
            else
                sheet.Cell(row, 6).Value = "Flexible";
            sheet.Cell(row, 7).Value = string.Join(", ", e.DaysOff.OrderBy(d => ((int)d + 1) % 7).Select(d => d.ToString()));
            sheet.Cell(row, 8).Value = e.HiredOn.ToDateTime(TimeOnly.MinValue);
            sheet.Cell(row, 9).Value = e.DeviceUserId ?? string.Empty;
            sheet.Cell(row, 10).Value = e.DailyRate;
            sheet.Cell(row, 11).Value = e.HourlyRate;
            row++;
        }

        var last = Math.Max(row - 1, 2);
        Formats(sheet.Range(2, 1, last, Headers.Length));
        sheet.Range(2, 10, last, 11).Style.NumberFormat.Format = "#,##0.00";
        sheet.Range(2, 6, last, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        var table = sheet.Range(1, 1, last, headers.Length).CreateTable("Employees");
        table.Theme = XLTableTheme.TableStyleMedium2;
        table.ShowAutoFilter = true;
        sheet.Row(1).Style.Font.Bold = true;
        sheet.Row(1).Style.Alignment.WrapText = true;
        for (var i = 0; i < headers.Length; i++)
            sheet.Column(i + 1).Width = i < Widths.Length ? Widths[i] : 18;
        sheet.SheetView.FreezeRows(1);
        return Saved(workbook);
    }

    // The rows of a filled-in sheet: the template's, or the first visible sheet headed with a
    // name. Blank rows, and rows that only ever had formatting, are left out.
    public static IReadOnlyList<EmployeeSheetRow> Read(Stream stream)
    {
        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(stream);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new DomainException("That file could not be read as an Excel workbook (.xlsx). Download the template, fill it in, and import that.");
        }

        using (workbook)
        {
            var (sheet, columns) = workbook.Worksheets
                .Where(s => s.Visibility == XLWorksheetVisibility.Visible)
                .OrderBy(s => s.Name == DataSheetName ? 0 : 1)
                .Select(s => (Sheet: s, Columns: Columns(s)))
                .FirstOrDefault(s => s.Columns is not null);
            if (sheet is null || columns is not { } c)
                throw new DomainException("No sheet has a Name heading. Download the template, fill it in, and import that.");

            var used = sheet.RowsUsed().Where(r => r.RowNumber() > 1).ToList();
            if (used.Count > MaxRows)
                throw new DomainException($"The sheet has {used.Count:N0} rows: import at most {MaxRows:N0} at a time.");
            var rows = new List<EmployeeSheetRow>();
            foreach (var r in used)
            {
                string? Cell(int? column) => column is { } at ? Text(r.Cell(at)) : null;
                var read = new EmployeeSheetRow(
                    r.RowNumber(), Cell(c[0]), Cell(c[1]), Cell(c[2]), Cell(c[3]), Cell(c[4]), Cell(c[5]), Cell(c[6]), Cell(c[7]), Cell(c[8]));
                if (new[] { read.Name, read.EmploymentType, read.Pay, read.Department, read.JobRole, read.ShiftStartsAt, read.DaysOff, read.HiredOn, read.DeviceUserId }
                    .Any(v => v is not null))
                    rows.Add(read);
            }

            return rows;
        }
    }

    private static void Heading(IXLWorksheet sheet)
    {
        for (var i = 0; i < Headers.Length; i++)
        {
            var cell = sheet.Cell(1, i + 1);
            cell.Value = Headers[i];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = XLColor.FromArgb(63, 81, 181);
            cell.Style.Alignment.WrapText = true;
            sheet.Column(i + 1).Width = Widths[i];
        }

        sheet.SheetView.FreezeRows(1);
    }

    // Each column's cells as they read: money, a time of day, a date, the device number as text
    // (so 007 stays 007).
    private static void Formats(IXLRange rows)
    {
        rows.Column(3).Style.NumberFormat.Format = "#,##0.00";
        rows.Column(6).Style.NumberFormat.Format = "hh:mm";
        rows.Column(8).Style.NumberFormat.Format = "yyyy-mm-dd";
        rows.Column(9).Style.NumberFormat.Format = "@";
    }

    private static void AddHelp(XLWorkbook workbook)
    {
        var help = workbook.Worksheets.Add(HelpSheetName);
        string[] lines =
        [
            "Fill in one employee a row on the Employees sheet, under the headings.",
            "املأ موظفاً في كل صف في ورقة Employees تحت العناوين.",
            "",
            "Name: required. A name already on the till is skipped, never added twice.",
            "Employment Type: Monthly or Daily (شهري أو يومي). Blank: Monthly.",
            "Base Salary: required. A month's salary, or a day's wage for daily pay.",
            "Department: one on the list, or a new one, which a manager's import adds. Blank: General.",
            "Job Role: required. One of the department's roles, or a new one, which is made.",
            "Shift Start: required. When their nine hours begin, 10:00 or 16:30; Flexible (مرن) for flexible hours.",
            "Days Off: their weekly days off, Friday, Saturday or الجمعة، السبت. Blank for none.",
            "Hired On: required. The day they started: their first month's pay counts from it.",
            "Device No.: optional. Their user number on the attendance device; a number already given is skipped.",
            "",
            "For example:",
        ];
        for (var i = 0; i < lines.Length; i++)
            help.Cell(i + 1, 1).Value = lines[i];
        help.Cell(1, 1).Style.Font.Bold = true;

        var first = lines.Length + 1;
        object[][] examples =
        [
            [.. Headers],
            ["Hassan Ali", "Monthly", 5400, "Kitchen", "Chef", "10:00", "Friday", "2026-09-01", "7"],
            ["منى سعيد", "Monthly", 4000, "Service", "Waiter", "16:00", "Friday, Saturday", "2026-10-01", "12"],
            ["Karim Adel", "Daily", 250, "Kitchen", "Helper", "Flexible", "", "2026-10-15", ""],
        ];
        for (var r = 0; r < examples.Length; r++)
            for (var c = 0; c < examples[r].Length; c++)
                help.Cell(first + r, c + 1).Value = XLCellValue.FromObject(examples[r][c]);
        help.Row(first).Style.Font.Bold = true;
        for (var i = 0; i < Widths.Length; i++)
            help.Column(i + 1).Width = Widths[i];
        help.Column(1).Width = 40;
    }

    // Where each column is, by its heading in the first row; null without a name column.
    private static int?[]? Columns(IXLWorksheet sheet)
    {
        var headings = sheet.Row(1).CellsUsed()
            .Select(c => (Column: c.Address.ColumnNumber, Parts: Parts(c.GetFormattedString())))
            .ToList();
        var taken = new HashSet<int>();
        int? Find(string[] names)
        {
            foreach (var (column, parts) in headings)
                if (!taken.Contains(column) && parts.Any(names.Contains))
                {
                    taken.Add(column);
                    return column;
                }

            return null;
        }

        // The specific headings first, so "Employee Name" is the name and not a type.
        var device = Find(DeviceNames);
        var hired = Find(HiredOnNames);
        var shift = Find(ShiftNames);
        var daysOff = Find(DaysOffNames);
        var type = Find(TypeNames);
        var pay = Find(PayNames);
        var department = Find(DepartmentNames);
        var role = Find(RoleNames);
        return Find(NameNames) is { } name ? [name, type, pay, department, role, shift, daysOff, hired, device] : null;
    }

    private static string[] Parts(string heading) =>
        heading.Split(['·', '(', ')', '/', '|', ':'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => string.Join(' ', p.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant())
            .ToArray();

    // A cell as written: a number in the invariant culture, a date as yyyy-MM-dd, a time of day as
    // HH:mm, anything else as Excel shows it.
    private static string? Text(IXLCell cell)
    {
        var value = cell.Value;
        var text = value.Type switch
        {
            XLDataType.Blank => null,
            XLDataType.Number => ((decimal)value.GetNumber()).ToString(Invariant),
            XLDataType.Text => value.GetText(),
            XLDataType.DateTime => When(value.GetDateTime()),
            XLDataType.TimeSpan => TimeOnly.FromTimeSpan(TimeSpan.FromTicks(value.GetTimeSpan().Ticks % TimeSpan.TicksPerDay)).ToString("HH:mm", Invariant),
            _ => cell.GetFormattedString(),
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    // A time cell is a date on Excel's first day; a date cell has no time of day.
    private static string When(DateTime value) =>
        value < new DateTime(1900, 1, 2) ? value.ToString("HH:mm", Invariant)
        : value.TimeOfDay == TimeSpan.Zero ? value.ToString("yyyy-MM-dd", Invariant)
        : value.ToString("yyyy-MM-dd HH:mm", Invariant);

    private static Stream Saved(XLWorkbook workbook)
    {
        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }
}
