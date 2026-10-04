using System.Text.Json.Serialization;
namespace Otantik.SharedKernel.Catalog;

// A named grouping inside one Category (e.g. "Hot Drinks" inside "Drinks"), rendered as
// an inline section headline on the storefront - never its own clickable card, unlike
// Category itself. Unlike MenuItem.Category (a free-text field, see that entity's own
// comment), this is a real foreign-key relationship: a MenuItem can only be tagged with
// a SubCategory that actually exists.
public class SubCategory
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
    public int DisplayOrder { get; set; }

    public int CategoryId { get; set; }
    public Category Category { get; set; } = null!;

    [JsonIgnore]
    public ICollection<MenuItem> MenuItems { get; set; } = new List<MenuItem>();
}
