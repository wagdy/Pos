using System.Globalization;
using ClosedXML.Excel;
using OtantikPos.Hr.Domain.Employees;
using OtantikPos.Hr.Domain.Payroll;
using OtantikPos.Inventory.Application.Hr;

namespace OtantikPos.Node.Infrastructure.Excel;

// A month's payroll (مسير الرواتب) for the accounts: each slip as the Payroll tab shows it, column
// by column from the base salary to the net payable, its status and what is paid of it, with the
// month's totals. Drafts as worked out when exported; finalized slips as they were finalized.
public static class PayrollWorkbook
{
    public const string ContentType = EmployeeWorkbook.ContentType;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private const string Money = "#,##0.00";
    private const int HeaderRow = 4;

    public static string FileName(DateOnly month) => $"payroll-{month.ToString("yyyy-MM", Invariant)}.xlsx";

    private static readonly (string Header, double Width, bool IsMoney)[] Columns =
    [
        ("Employee · الموظف", 28, false),
        ("Job · الوظيفة", 18, false),
        ("Department · القسم", 18, false),
        ("Pay · نوع العمالة", 14, false),
        ("Days employed · أيام التعيين", 14, false),
        ("Days worked · أيام العمل", 14, false),
        ("Base salary · الراتب الأساسي", 16, true),
        ("Incentives · حوافز", 14, true),
        ("Leave allowances · بدل إجازات", 16, true),
        ("Overtime · إضافي", 14, true),
        ("Gross · إجمالي المستحق", 16, true),
        ("Advances · السلفيات", 14, true),
        ("Lateness · خصم التأخير", 14, true),
        ("Absence · خصم الغياب", 14, true),
        ("Unpaid leave · إجازة بدون راتب", 14, true),
        ("Penalties · جزاءات إدارية", 14, true),
        ("Last month · مرحل", 14, true),
        ("Net payable · صافي المستحق", 16, true),
        ("To next month · مرحل للشهر القادم", 16, true),
        ("Status · الحالة", 16, false),
        ("Paid · المدفوع", 14, true),
        ("Still to pay · المتبقي", 14, true),
    ];

    public static Stream Export(PayrollMonthDto payroll, IReadOnlyDictionary<Guid, string> departments, DateOnly exportedOn)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Payroll");
        var title = sheet.Cell(1, 1);
        title.Value = $"Payroll · مسير الرواتب — {payroll.Month.ToString("MMMM yyyy", Invariant)}";
        title.Style.Font.Bold = true;
        title.Style.Font.FontSize = 14;
        var note = sheet.Cell(2, 1);
        note.Value = $"Exported {exportedOn.ToString("d MMM yyyy", Invariant)}: drafts as worked out then, finalized slips as they were finalized.";
        note.Style.Font.Italic = true;
        note.Style.Font.FontColor = XLColor.Gray;

        for (var i = 0; i < Columns.Length; i++)
        {
            sheet.Cell(HeaderRow, i + 1).Value = Columns[i].Header;
            sheet.Column(i + 1).Width = Columns[i].Width;
        }

        var row = HeaderRow + 1;
        foreach (var slip in payroll.Slips.OrderBy(s => departments.GetValueOrDefault(s.Employee.DepartmentId)).ThenBy(s => s.Employee.Name))
        {
            var f = slip.Figures;
            var finalized = slip.Status == PayrollSlipStatus.Finalized;
            XLCellValue[] values =
            [
                slip.Employee.Name,
                slip.Employee.JobTitle,
                departments.GetValueOrDefault(slip.Employee.DepartmentId) ?? string.Empty,
                f.EmploymentType == EmploymentType.Daily ? "Daily" : "Monthly",
                $"{f.DaysEmployed} of {f.DaysInMonth}",
                f.DaysWorked,
                f.BaseSalary,
                f.Incentives,
                f.LeaveAllowances + f.DaysOffWorkedPay + f.HolidaysWorkedPay,
                f.OvertimePay,
                f.Gross,
                f.Advances,
                f.LatenessPenalty,
                f.AbsencePenalty,
                f.UnpaidLeaveDeduction,
                f.AdministrativePenalty,
                f.BroughtForward,
                f.NetPayable,
                f.CarriedForward,
                slip.Status switch
                {
                    PayrollSlipStatus.Finalized => "Finalized · نهائي",
                    PayrollSlipStatus.Draft => "Draft · مسودة",
                    _ => "Not calculated · لم يُحتسب",
                },
                finalized ? (XLCellValue)slip.SalaryPaid : Blank.Value,
                finalized ? (XLCellValue)slip.SalaryOwed : Blank.Value,
            ];
            for (var i = 0; i < values.Length; i++)
                sheet.Cell(row, i + 1).Value = values[i];
            row++;
        }

        if (payroll.Slips.Count == 0)
        {
            sheet.Cell(HeaderRow + 1, 1).Value = "No one was employed this month.";
            Style(sheet.Range(HeaderRow, 1, HeaderRow, Columns.Length));
            return Saved(workbook);
        }

        var last = row - 1;
        for (var i = 0; i < Columns.Length; i++)
            if (Columns[i].IsMoney)
                sheet.Range(HeaderRow + 1, i + 1, last + 1, i + 1).Style.NumberFormat.Format = Money;

        // The month's totals under the slips: a sum of each amount.
        var table = sheet.Range(HeaderRow, 1, last, Columns.Length).CreateTable("Payroll");
        table.Theme = XLTableTheme.TableStyleMedium2;
        table.ShowAutoFilter = true;
        table.ShowTotalsRow = true;
        table.Field(0).TotalsRowLabel = "Total · الإجمالي";
        for (var i = 0; i < Columns.Length; i++)
            if (Columns[i].IsMoney)
                table.Field(i).TotalsRowFunction = XLTotalsRowFunction.Sum;
        sheet.Row(HeaderRow).Style.Alignment.WrapText = true;
        sheet.Row(HeaderRow).Style.Font.Bold = true;
        sheet.Row(last + 1).Style.Font.Bold = true;
        sheet.SheetView.Freeze(HeaderRow, 1);
        return Saved(workbook);
    }

    private static void Style(IXLRange header)
    {
        header.Style.Font.Bold = true;
        header.Style.Font.FontColor = XLColor.White;
        header.Style.Fill.BackgroundColor = XLColor.FromArgb(63, 81, 181);
        header.Style.Alignment.WrapText = true;
    }

    private static Stream Saved(XLWorkbook workbook)
    {
        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }
}
