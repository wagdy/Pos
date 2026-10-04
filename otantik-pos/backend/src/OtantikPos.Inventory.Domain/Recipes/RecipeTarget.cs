namespace OtantikPos.Inventory.Domain.Recipes;

public enum RecipeTargetKind
{
    MenuItem,
    AddOn,
}

// What a recipe is for, named by the shared catalog's ids: the delivery system's MenuItem, one
// of its MenuItemVariants, or an AddOn. Plain ints. Inventory never loads the catalog, so it
// cannot come to depend on it.
public readonly record struct RecipeTarget(RecipeTargetKind Kind, int CatalogItemId, int? VariantId)
{
    // The base recipe, used for every variant that does not have its own.
    public static RecipeTarget ForMenuItem(int menuItemId) => new(RecipeTargetKind.MenuItem, menuItemId, null);

    // For a variant that uses different quantities, such as a 1 kg tray against a half.
    public static RecipeTarget ForVariant(int menuItemId, int variantId) => new(RecipeTargetKind.MenuItem, menuItemId, variantId);

    // Per unit of the item it is added to, the way its price is.
    public static RecipeTarget ForAddOn(int addOnId) => new(RecipeTargetKind.AddOn, addOnId, null);
}
