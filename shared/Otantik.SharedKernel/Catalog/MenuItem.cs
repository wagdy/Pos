using System.Text.Json.Serialization;
namespace Otantik.SharedKernel.Catalog;

public class MenuItem
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
    public string? Description { get; set; }
    public decimal Price { get; set; }
    public string Category { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public bool IsAvailable { get; set; } = true;

    // Soft delete. A global query filter (see ApplicationDbContext.OnModelCreating) hides
    // these from every ordinary query, so the customer menu and the admin catalog do not
    // need to know about the flag at all. Deleting for real is not an option here: order
    // history references menu items, and Category is matched by free-text name on every
    // item that uses it.
    public bool IsDeleted { get; set; }

    // What to show INSTEAD of a price when Price is 0 - "السعر بناء على الوزن" for meat
    // and fish sold by weight, where the figure is not known until the item is weighed.
    //
    // Only meaningful while Price is 0; the admin form clears it as soon as a real price
    // is entered, so an item never carries both a price and a note explaining why it has
    // none. Purely a display string - it is not parsed and never becomes a charge.
    public string? PriceNote { get; set; }

    // "This item's price IS the sum of its add-ons" - a kilo of mixed grill where the
    // customer picks the cuts, and the total is whatever they choose.
    //
    // Price is forced to 0 when this is set, and the item MUST have at least one add-on
    // (enforced on create and update) - otherwise there is nothing for the total to be
    // built from and the line would come to zero. The customer-facing half of that rule
    // lives in OrderService: at least one add-on must actually be SELECTED, since having
    // add-ons available and choosing none still adds up to nothing.
    public bool IsPriceBasedOnAddons { get; set; }

    // Promotional offer: Price is the sale price the customer pays, OriginalPrice is the
    // struck-through "was" figure, DiscountPercentage is the badge.
    //
    // Price stays the single source of what is charged - nothing downstream (cart,
    // OrderService.BuildOrderItemsAsync, the receipt) needs to know an offer exists. That
    // is the point: an offer is presentation over the same price, not a second pricing
    // path to keep in step.
    //
    // Only valid on a simple-priced item. An item with variants never charges Price (the
    // chosen variant's price is charged), and a dynamically-priced one forces Price to 0 -
    // in both cases a "was 120, now 100" line would be comparing against a number nobody
    // pays. MenuItemService refuses those combinations rather than storing a claim the
    // storefront would render as nonsense.
    public bool IsOffer { get; set; }

    // Null unless IsOffer. Must exceed Price: a "was" price at or below what is being
    // charged is either meaningless or a false saving.
    public decimal? OriginalPrice { get; set; }

    // Null unless IsOffer. Derived from the two prices when the admin does not supply it,
    // but deliberately STORED rather than computed at read time, because the admin is
    // allowed to overwrite it - a 33% saving advertised as "-30%" is a normal thing to
    // want, and recomputing would silently overwrite that choice.
    public int? DiscountPercentage { get; set; }


    // Optional finer-grained grouping within Category above (e.g. "Hot Drinks" inside
    // "Drinks") - unlike Category, this is a real FK, not free text, since SubCategory
    // only exists as a row created through the SubCategories API.
    public int? SubCategoryId { get; set; }
    public SubCategory? SubCategory { get; set; }

    [JsonIgnore]
    public ICollection<OrderItem> OrderItems { get; set; } = new List<OrderItem>();
    public ICollection<MenuItemAddOn> MenuItemAddOns { get; set; } = new List<MenuItemAddOn>();

    // Sizes/weights. Empty for most items; when non-empty, Price above is never what the
    // customer pays - the chosen variant's price is. See MenuItemVariant.
    public ICollection<MenuItemVariant> Variants { get; set; } = new List<MenuItemVariant>();
}
