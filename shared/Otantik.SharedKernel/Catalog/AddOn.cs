using System.Text.Json.Serialization;
namespace Otantik.SharedKernel.Catalog;

// Admin-managed catalog entry (e.g. "Extra Sauce", "Side Rice"), assigned
// per menu item via MenuItemAddOn rather than being available on every item.
public class AddOn
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    // Optional Arabic display name. Null/blank means "no Arabic name was entered", and
    // the client falls back to Name - see LocalNamePipe on the frontend. Deliberately a
    // separate nullable column rather than a translations table: the app has exactly two
    // languages and one translatable field per row, and a join table would buy nothing
    // but a join. Note that Name stays the identity used for lookups and grouping
    // (MenuItem.Category matches Category.Name as free text), so this column is display
    // only - nothing keys off it.
    public string? NameAr { get; set; }
    public decimal Price { get; set; }

    [JsonIgnore]
    public ICollection<MenuItemAddOn> MenuItemAddOns { get; set; } = new List<MenuItemAddOn>();
}
