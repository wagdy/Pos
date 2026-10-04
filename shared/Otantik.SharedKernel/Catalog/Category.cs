namespace Otantik.SharedKernel.Catalog;

public class Category
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

    // Soft delete. A global query filter (see ApplicationDbContext.OnModelCreating) hides
    // these from every ordinary query, so the customer menu and the admin catalog do not
    // need to know about the flag at all. Deleting for real is not an option here: order
    // history references menu items, and Category is matched by free-text name on every
    // item that uses it.
    public bool IsDeleted { get; set; }

    public string? ImageUrl { get; set; }
}
