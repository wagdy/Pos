using System.Globalization;
using System.Text;

namespace OtantikPos.Inventory.Application.Common;

// Cells of an imported spreadsheet, as a person reads them.
internal static class SheetText
{
    // Trimmed, runs of spaces made one, and null when empty.
    public static string? Clean(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // A number as written, in Western or Arabic digits, with or without thousands separators.
    public static decimal? Amount(string text) =>
        decimal.TryParse(Western(text), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) ? amount : null;

    // Arabic digits, and their decimal and thousands marks, written as Western ones.
    public static string Western(string text)
    {
        var western = new StringBuilder(text.Length);
        foreach (var c in text)
            western.Append(c switch
            {
                >= '٠' and <= '٩' => (char)('0' + (c - '٠')),
                >= '۰' and <= '۹' => (char)('0' + (c - '۰')),
                '٫' => '.',
                '٬' => ',',
                _ => c,
            });
        return western.ToString();
    }

    // A name as two spellings of it are compared: capitals and spacing aside, an underscore as a
    // space, without the stretching tatweel, and the Arabic letters written more than one way
    // (أ إ آ ا, ة ه, ى ي) as one. زبدة_فيرن_ and زبده فيرن are the same butter.
    public static string Key(string name)
    {
        var plain = new StringBuilder(name.Length);
        foreach (var c in name)
            plain.Append(c switch
            {
                '_' => ' ',
                'ـ' => '\0',
                'أ' or 'إ' or 'آ' => 'ا',
                'ة' => 'ه',
                'ى' => 'ي',
                _ => c,
            });
        return Clean(plain.ToString().Replace("\0", string.Empty))?.ToUpperInvariant() ?? string.Empty;
    }
}
