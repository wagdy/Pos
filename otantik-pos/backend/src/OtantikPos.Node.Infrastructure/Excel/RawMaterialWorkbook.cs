using System.Globalization;
using ClosedXML.Excel;
using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Application.RawMaterials;

namespace OtantikPos.Node.Infrastructure.Excel;

// The raw materials spreadsheet: the template to fill in, and reading one back for the import
// (ImportRawMaterialsCommand, which decides what each row becomes). Plain data in and out, so it
// can be checked without a database.
//
// Columns are found by their heading, in English or Arabic, not by position: a sheet with its
// columns moved, or headed in Arabic only, still imports as written.
public static class RawMaterialWorkbook
{
    public const string FileName = "raw-materials-template.xlsx";
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string DataSheetName = "Raw Materials";
    private const string HelpSheetName = "How to fill";
    private const string ListsSheetName = "Lists";

    public const string NameHeader = "Material Name · اسم الخامة";
    public const string CategoryHeader = "Category · القسم";
    public const string UnitHeader = "Unit of Measure · وحدة القياس";
    public const string CostHeader = "Initial Cost · التكلفة المبدئية";

    // How each column may be headed. A heading matches when any part of it, split at "·" or a
    // bracket, is one of these, capitals and spacing aside.
    private static readonly string[] NameNames = ["material name", "material", "name", "اسم الخامة", "الخامة", "الاسم"];
    private static readonly string[] CategoryNames = ["category", "القسم", "الفئة"];
    private static readonly string[] UnitNames = ["unit of measure", "unit", "uom", "وحدة القياس", "الوحدة"];
    private static readonly string[] CostNames = ["initial cost", "cost", "التكلفة المبدئية", "التكلفة"];

    // Rows the dropdowns and checks cover, past which a sheet still imports without them.
    private const int FormattedRows = 1000;

    // Most a sheet may hold: a restaurant's materials are hundreds, not tens of thousands.
    private const int MaxRows = 5000;

    // The template: the four columns with dropdowns for the category (the restaurant's own, a new
    // name allowed) and the unit, a sheet of how to fill it with examples, and a hidden sheet of
    // the lists. No example rows on the sheet itself: left in, they would be imported.
    public static Stream Build(IReadOnlyList<string> categories)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(DataSheetName);
        string[] headers = [NameHeader, CategoryHeader, UnitHeader, CostHeader];
        for (var i = 0; i < headers.Length; i++)
        {
            var cell = sheet.Cell(1, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = XLColor.FromArgb(63, 81, 181);
        }

        sheet.Cell(1, 4).CreateComment().AddText("Per kg, litre or piece, as the unit says. Optional: leave it blank when not known.");
        sheet.Column(1).Width = 34;
        sheet.Column(2).Width = 24;
        sheet.Column(3).Width = 26;
        sheet.Column(4).Width = 28;
        sheet.Range(2, 4, FormattedRows + 1, 4).Style.NumberFormat.Format = "0.00";
        sheet.SheetView.FreezeRows(1);

        var lists = workbook.Worksheets.Add(ListsSheetName);
        lists.Cell(1, 1).Value = "Categories";
        lists.Cell(1, 2).Value = "Units";
        for (var i = 0; i < categories.Count; i++)
            lists.Cell(i + 2, 1).Value = categories[i];
        for (var i = 0; i < SheetUnits.Names.Count; i++)
            lists.Cell(i + 2, 2).Value = SheetUnits.Names[i];
        lists.Visibility = XLWorksheetVisibility.Hidden;

        // A category not on the list is allowed: it is added on import.
        var lastCategory = Math.Max(categories.Count, 1) + 1;
        var category = sheet.Range(2, 2, FormattedRows + 1, 2).CreateDataValidation();
        category.List($"'{ListsSheetName}'!$A$2:$A${lastCategory}", true);
        category.IgnoreBlanks = true;
        category.ErrorStyle = XLErrorStyle.Information;
        category.ErrorTitle = "A new category";
        category.ErrorMessage = "Not on the list yet: it is added on import. Leave it blank for General.";

        // Arabic units are taken too, so a typed كيلو is warned about, not refused.
        var unit = sheet.Range(2, 3, FormattedRows + 1, 3).CreateDataValidation();
        unit.List($"'{ListsSheetName}'!$B$2:$B${SheetUnits.Names.Count + 1}", true);
        unit.IgnoreBlanks = true;
        unit.ErrorStyle = XLErrorStyle.Warning;
        unit.ErrorTitle = "Kg, Liter or Piece";
        unit.ErrorMessage = "The till takes Kg, Liter or Piece (كيلو، لتر، قطعة).";

        var cost = sheet.Range(2, 4, FormattedRows + 1, 4).CreateDataValidation();
        cost.Decimal.EqualOrGreaterThan(0);
        cost.IgnoreBlanks = true;
        cost.ErrorStyle = XLErrorStyle.Stop;
        cost.ErrorTitle = "A cost";
        cost.ErrorMessage = "Write the cost as a number, 0 or more: per kg, litre or piece.";

        AddHelp(workbook);
        sheet.SetTabActive();

        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }

    private static void AddHelp(XLWorkbook workbook)
    {
        var help = workbook.Worksheets.Add(HelpSheetName);
        string[] lines =
        [
            "Fill in one material a row on the Raw Materials sheet, under the headings.",
            "املأ خامة في كل صف في ورقة Raw Materials تحت العناوين.",
            "",
            "Material Name: required. A name already on the list is skipped, never added twice.",
            "Category: one on the list, or a new one, which is added. Blank: General.",
            "Unit of Measure: Kg, Liter or Piece: how it is bought and priced.",
            "Initial Cost: optional, per kg, litre or piece. The price to start from; purchases move it from there.",
            "",
            "For example:",
        ];
        for (var i = 0; i < lines.Length; i++)
            help.Cell(i + 1, 1).Value = lines[i];
        help.Cell(1, 1).Style.Font.Bold = true;

        var first = lines.Length + 1;
        object[][] examples =
        [
            [NameHeader, CategoryHeader, UnitHeader, CostHeader],
            ["Chicken breast", "Poultry", "Kg", 185],
            ["Full-cream milk", "Dairy", "Liter", 42.5],
            ["Eggs", "Dairy", "Piece", 4.25],
            ["بقدونس", "Vegetables", "Kg", ""],
        ];
        for (var r = 0; r < examples.Length; r++)
            for (var c = 0; c < examples[r].Length; c++)
                help.Cell(first + r, c + 1).Value = XLCellValue.FromObject(examples[r][c]);
        help.Row(first).Style.Font.Bold = true;
        help.Column(1).Width = 34;
        help.Column(2).Width = 24;
        help.Column(3).Width = 26;
        help.Column(4).Width = 28;
    }

    // The rows of a filled-in sheet: the template's, or the first visible sheet headed with a
    // material name. Blank rows, and rows that only ever had formatting, are left out.
    public static IReadOnlyList<RawMaterialSheetRow> Read(Stream stream)
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
            if (sheet is null || columns is not { } found)
                throw new DomainException("No sheet has a Material Name heading. Download the template, fill it in, and import that.");

            var used = sheet.RowsUsed().Where(r => r.RowNumber() > 1).ToList();
            if (used.Count > MaxRows)
                throw new DomainException($"The sheet has {used.Count:N0} rows: import at most {MaxRows:N0} at a time.");
            var rows = new List<RawMaterialSheetRow>();
            foreach (var row in used)
            {
                string? Cell(int? column) => column is { } c ? Text(row.Cell(c)) : null;
                var read = new RawMaterialSheetRow(row.RowNumber(), Cell(found.Name), Cell(found.Category), Cell(found.Unit), Cell(found.Cost));
                if (read is { Name: not null } or { Category: not null } or { Unit: not null } or { InitialCost: not null })
                    rows.Add(read);
            }

            return rows;
        }
    }

    // Where each column is, by its heading in the first row; null without a material name column.
    private static (int Name, int? Category, int? Unit, int? Cost)? Columns(IXLWorksheet sheet)
    {
        var headings = sheet.Row(1).CellsUsed()
            .Select(c => (Column: c.Address.ColumnNumber, Parts: Parts(c.GetFormattedString())))
            .ToList();
        int? Find(string[] names) => headings.Where(h => h.Parts.Any(names.Contains)).Select(h => (int?)h.Column).FirstOrDefault();
        return Find(NameNames) is { } name ? (name, Find(CategoryNames), Find(UnitNames), Find(CostNames)) : null;
    }

    private static string[] Parts(string heading) =>
        heading.Split(['·', '(', ')', '/', '|', ':'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => string.Join(' ', p.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant())
            .ToArray();

    // A cell as written: a number in the invariant culture, anything else as Excel shows it.
    private static string? Text(IXLCell cell)
    {
        var value = cell.Value;
        var text = value.IsBlank ? null
            : value.IsNumber ? ((decimal)value.GetNumber()).ToString(CultureInfo.InvariantCulture)
            : value.IsText ? value.GetText()
            : cell.GetFormattedString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}
