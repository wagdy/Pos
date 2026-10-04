using System.Text.Json.Serialization;
namespace Otantik.SharedKernel.Catalog;

// One purchasable size/weight of a menu item - "500g", "1 Kilo", "Family tray".
//
// Price is ABSOLUTE, not a delta on MenuItem.Price. A variant replaces the base price
// rather than adding to it, which is what separates a variant from an add-on: picking
// "1 Kilo" means the item costs that, while adding tahini means the item costs its own
// price plus tahini. Storing a delta would have made "1 Kilo" meaningless the moment
// someone edited the base price.
//
// When an item has any variants at all, ordering it without choosing one is refused
// server-side - see OrderService.BuildOrderItemsAsync. There is deliberately no
// "default" flag: a default would let a mispriced line through silently if the UI ever
// failed to send a choice.
public class MenuItemVariant
{
    public int Id { get; set; }

    public int MenuItemId { get; set; }
    [JsonIgnore]
    public MenuItem MenuItem { get; set; } = null!;

    public string Name { get; set; } = string.Empty;

    // Optional Arabic display name, same fallback rule as MenuItem.NameAr - blank falls
    // back to Name. See LocalNamePipe on the client.
    public string? NameAr { get; set; }

    public decimal Price { get; set; }

    public int DisplayOrder { get; set; }

    // Lets a single size go out of stock without deleting it and losing its identity in
    // the order history.
    public bool IsAvailable { get; set; } = true;
}
