using System.Text.Json.Serialization;
namespace Otantik.SharedKernel.Catalog;

// Join row: which add-ons an admin has assigned as available on a given menu item.
public class MenuItemAddOn
{
    public int MenuItemId { get; set; }
    [JsonIgnore]
    public MenuItem MenuItem { get; set; } = null!;

    public int AddOnId { get; set; }
    public AddOn AddOn { get; set; } = null!;
}
