namespace Otantik.SharedKernel.Orders;

public sealed record ItemBuildResult(OrderItem? Item, string? Error)
{
    public static ItemBuildResult Failed(string error) => new(null, error);
}

// Turns a menu choice into a priced OrderItem: the delivery system's
// OrderService.BuildOrderItemsAsync rules, moved here so a captain's order built in the cloud
// and a cashier's order built at the till price the same choice the same way.
//
// The rules, with that method's reasons:
//   An item with sizes must name one of its own, available sizes. An item with no variants
//   has no meaningful base price to fall back on, and naming another item's cheaper size must
//   fail rather than underprice.
//   An item without sizes must not name one: the client is confused about what it is ordering.
//   A variant's price replaces the base price; add-ons are what add.
//   Add-ons must be offered on this item.
//   An item priced from its add-ons needs at least one paid add-on.
//   An item priced on the day (price 0) has no figure to charge and is refused.
//
// The menu item must be passed in with Variants and MenuItemAddOns (with their AddOns) loaded.
public static class OrderItemBuilder
{
    public static ItemBuildResult Build(MenuItem menuItem, int? variantId, IEnumerable<int> addOnIds, int quantity)
    {
        if (!menuItem.IsAvailable || menuItem.IsDeleted)
            return ItemBuildResult.Failed($"'{menuItem.Name}' is currently unavailable.");

        MenuItemVariant? variant = null;
        if (menuItem.Variants.Count > 0)
        {
            if (variantId is null)
                return ItemBuildResult.Failed($"Please choose a size for '{menuItem.Name}'.");

            variant = menuItem.Variants.FirstOrDefault(v => v.Id == variantId.Value);
            if (variant is null)
                return ItemBuildResult.Failed($"The selected size is not available for '{menuItem.Name}'.");
            if (!variant.IsAvailable)
                return ItemBuildResult.Failed($"'{variant.Name}' of '{menuItem.Name}' is currently unavailable.");
        }
        else if (variantId is not null)
        {
            return ItemBuildResult.Failed($"'{menuItem.Name}' does not come in different sizes.");
        }

        var unitPrice = variant?.Price ?? menuItem.Price;

        var offered = menuItem.MenuItemAddOns.ToDictionary(ma => ma.AddOnId, ma => ma.AddOn);
        var addOns = new List<OrderItemAddOn>();
        foreach (var addOnId in addOnIds.Distinct())
        {
            if (!offered.TryGetValue(addOnId, out var addOn))
                return ItemBuildResult.Failed($"The selected add-on is not available for '{menuItem.Name}'.");

            addOns.Add(new OrderItemAddOn { AddOnId = addOn.Id, Name = addOn.Name, Price = addOn.Price });
        }

        if (menuItem.IsPriceBasedOnAddons)
        {
            if (addOns.Sum(a => a.Price) <= 0)
                return ItemBuildResult.Failed($"Please choose at least one paid option for '{menuItem.Name}'.");
        }
        else if (unitPrice <= 0)
        {
            // The delivery system words this for online customers ("call the branch"). Here it
            // has to make sense to a cashier too.
            return ItemBuildResult.Failed($"'{menuItem.Name}' is priced on the day and has no fixed price to charge.");
        }

        return new ItemBuildResult(new OrderItem
        {
            MenuItemId = menuItem.Id,
            MenuItemName = menuItem.Name,
            VariantId = variant?.Id,
            VariantName = variant?.Name,
            Quantity = quantity,
            UnitPrice = unitPrice,
            AddOns = addOns,
        }, null);
    }
}
