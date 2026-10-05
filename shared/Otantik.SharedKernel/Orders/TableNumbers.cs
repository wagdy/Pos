namespace Otantik.SharedKernel.Orders;

// A table's number as both systems keep it: trimmed, and in Western digits. A phone or tablet
// with an Arabic keyboard types ١٢ for table 12. Kept as typed, the one-open-order-per-table
// check took it for a different table from "12", and the kitchen printer, which has no Arabic,
// printed "Table ??".
public static class TableNumbers
{
    public static string? Normalize(string? tableNumber) =>
        string.IsNullOrWhiteSpace(tableNumber) ? null : WesternDigits(tableNumber.Trim());

    // Arabic-Indic digits, and the Persian ones some keyboards type, as 0-9: the same numbers.
    // Everything else as it is, so the text keeps its length.
    public static string WesternDigits(string text) =>
        string.Create(text.Length, text, static (digits, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                digits[i] = source[i] switch
                {
                    >= '٠' and <= '٩' => (char)('0' + (source[i] - '٠')),
                    >= '۰' and <= '۹' => (char)('0' + (source[i] - '۰')),
                    var other => other,
                };
            }
        });
}
