using System.Text.Json.Serialization;

namespace Otantik.SharedKernel.Orders;

public class OrderItemAddOn
{
    public int Id { get; set; }

    public int OrderItemId { get; set; }

    [JsonIgnore]
    public OrderItem OrderItem { get; set; } = null!;

    public int AddOnId { get; set; }

    [JsonIgnore]
    public AddOn AddOn { get; set; } = null!;

    // Snapshotted at order time so historical orders stay accurate even if the
    // add-on's name or price changes later — same pattern as OrderItem.UnitPrice.
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
}
