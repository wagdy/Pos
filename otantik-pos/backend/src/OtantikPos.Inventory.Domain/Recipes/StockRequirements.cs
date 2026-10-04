namespace OtantikPos.Inventory.Domain.Recipes;

// One sold order line, as inventory sees it: catalog ids and a quantity. Nothing about the
// order, its price or its customer. The POS turns its shared OrderItem into this before
// handing it over, which is the whole interface between the two modules.
public sealed record SoldItem(int MenuItemId, int? VariantId, IReadOnlyList<int> AddOnIds, int Quantity);

public static class StockRequirements
{
    // What `item` draws from stock, summed per raw material:
    //   the variant's own recipe if it has one, otherwise the menu item's base recipe;
    //   plus each add-on's recipe, per unit, the same way the add-on is priced per unit;
    //   all multiplied by the quantity.
    // The variant recipe replaces the base rather than adding to it: a 1 kg tray is not a
    // base portion plus a tray.
    public static IReadOnlyDictionary<Guid, decimal> For(SoldItem item, IReadOnlyCollection<Recipe> recipes)
    {
        var byTarget = recipes.ToDictionary(r => r.Target);
        var totals = new Dictionary<Guid, decimal>();

        var main = item.VariantId is { } variantId && byTarget.TryGetValue(RecipeTarget.ForVariant(item.MenuItemId, variantId), out var forVariant)
            ? forVariant
            : byTarget.GetValueOrDefault(RecipeTarget.ForMenuItem(item.MenuItemId));

        Add(main);
        foreach (var addOnId in item.AddOnIds)
            Add(byTarget.GetValueOrDefault(RecipeTarget.ForAddOn(addOnId)));

        return totals;

        void Add(Recipe? recipe)
        {
            if (recipe is null)
                return;

            foreach (var (rawMaterialId, quantity) in recipe.RequirementsFor(item.Quantity))
                totals[rawMaterialId] = totals.GetValueOrDefault(rawMaterialId) + quantity;
        }
    }

    // Every recipe the given items could need, so they can be loaded in one query.
    public static IReadOnlyCollection<RecipeTarget> TargetsFor(IEnumerable<SoldItem> items) =>
        items
            .SelectMany(i => new[] { RecipeTarget.ForMenuItem(i.MenuItemId) }
                .Concat(i.VariantId is { } v ? [RecipeTarget.ForVariant(i.MenuItemId, v)] : [])
                .Concat(i.AddOnIds.Select(RecipeTarget.ForAddOn)))
            .ToHashSet();
}
