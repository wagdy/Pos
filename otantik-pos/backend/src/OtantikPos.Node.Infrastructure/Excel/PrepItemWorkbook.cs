using ClosedXML.Excel;
using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Application.Production;

namespace OtantikPos.Node.Infrastructure.Excel;

// A spreadsheet of batch recipes (المصنعات), as kitchens and older systems write them: each prep
// item's name on a row of its own, a heading row (الصنف | الوحدة | الكمية), then its ingredients,
// one a row, with the unit and the quantity for one batch. Read as plain data for
// ImportPrepItemsCommand, which decides what each becomes.
//
// The columns are found from the first heading row, in English or Arabic; without one they are
// the first three. A row with only a name starts a prep item; a heading row is passed over.
public static class PrepItemWorkbook
{
    private static readonly string[] ItemNames = ["الصنف", "الخامه", "المكون", "item", "ingredient", "material"];
    private static readonly string[] UnitNames = ["الوحده", "وحده القياس", "unit", "uom", "unit of measure"];
    private static readonly string[] QuantityNames = ["الكميه", "quantity", "qty"];

    // Most a sheet may hold: a kitchen's recipes run to hundreds of rows, not tens of thousands.
    private const int MaxRows = 20_000;

    public static IReadOnlyList<PrepSheetItem> Read(Stream stream)
    {
        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(stream);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new DomainException("That file could not be read as an Excel workbook (.xlsx).");
        }

        using (workbook)
        {
            var sheets = workbook.Worksheets.Where(s => s.Visibility == XLWorksheetVisibility.Visible).ToList();
            var (sheet, columns) = sheets.Select(s => (Sheet: s, Columns: Columns(s))).FirstOrDefault(s => s.Columns is not null);
            sheet ??= sheets.FirstOrDefault() ?? throw new DomainException("The workbook has no sheet to read.");
            var (item, unit, quantity) = columns ?? (1, 2, 3);

            var used = sheet.RowsUsed().ToList();
            if (used.Count > MaxRows)
                throw new DomainException($"The sheet has {used.Count:N0} rows: import at most {MaxRows:N0} at a time.");

            var items = new List<PrepSheetItem>();
            (int Row, string? Name, List<PrepSheetLine> Lines)? current = null;
            void Close()
            {
                if (current is { } open)
                    items.Add(new PrepSheetItem(open.Row, open.Name, open.Lines));
            }

            foreach (var row in used)
            {
                var (a, b, c) = (Text(row.Cell(item)), Text(row.Cell(unit)), Text(row.Cell(quantity)));
                if (a is null && b is null && c is null || IsHeading(a, b, c))
                    continue;
                if (a is not null && b is null && c is null)
                {
                    Close();
                    current = (row.RowNumber(), a, []);
                    continue;
                }

                // Ingredients before any prep item's name: kept apart, nameless, to be said so.
                current ??= (row.RowNumber(), null, []);
                current.Value.Lines.Add(new PrepSheetLine(row.RowNumber(), a, b, c));
            }

            Close();
            return items;
        }
    }

    // The item, unit and quantity columns, from the first row that heads them; null without one.
    private static (int Item, int Unit, int Quantity)? Columns(IXLWorksheet sheet)
    {
        foreach (var row in sheet.RowsUsed().Take(50))
        {
            var cells = row.CellsUsed().Select(c => (Column: c.Address.ColumnNumber, Text: Plain(c.GetFormattedString()))).ToList();
            int? Find(string[] names) => cells.Where(c => names.Contains(c.Text)).Select(c => (int?)c.Column).FirstOrDefault();
            if (Find(ItemNames) is { } item && Find(UnitNames) is { } unit && Find(QuantityNames) is { } quantity)
                return (item, unit, quantity);
        }

        return null;
    }

    private static bool IsHeading(string? item, string? unit, string? quantity) =>
        item is not null && ItemNames.Contains(Plain(item)) && (unit is null || UnitNames.Contains(Plain(unit)));

    // A heading as compared: lower case, spacing aside, ة as ه.
    private static string Plain(string text) =>
        string.Join(' ', text.Replace('ة', 'ه').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    // A cell as written: a number in the invariant culture, whatever the machine's; anything else as
    // Excel shows it, trimmed. Null when empty.
    private static string? Text(IXLCell cell)
    {
        var value = cell.Value;
        var text = value.IsNumber ? ((decimal)value.GetNumber()).ToString(System.Globalization.CultureInfo.InvariantCulture) : cell.GetFormattedString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}
