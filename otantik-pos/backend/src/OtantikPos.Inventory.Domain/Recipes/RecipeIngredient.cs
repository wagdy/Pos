using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Domain.RawMaterials;

namespace OtantikPos.Inventory.Domain.Recipes;

public sealed class RecipeIngredient : Entity
{
    private RecipeIngredient() { }

    internal RecipeIngredient(Guid recipeId, Guid rawMaterialId, decimal quantity, decimal yieldPercent)
    {
        RecipeId = recipeId;
        RawMaterialId = rawMaterialId;
        Change(quantity, yieldPercent);
    }

    public Guid RecipeId { get; private set; }
    public Guid RawMaterialId { get; private set; }

    // The Edible Portion (EP) the recipe calls for, as written for all its portions, in the raw
    // material's own unit: grams, millilitres or pieces.
    public decimal Quantity { get; private set; }

    // The share of the material left after trimming. What stock gives up for Quantity is
    // Quantity ÷ Yield: 85 g of peppers to use at 85% takes 100 g off the shelf.
    public decimal YieldPercent { get; private set; } = 100;

    // As Purchased (AP): what comes off stock for the whole recipe.
    public decimal AsPurchasedQuantity => Quantity * 100 / YieldPercent;

    internal void Change(decimal quantity, decimal yieldPercent)
    {
        if (quantity <= 0)
            throw new DomainException("Ingredient quantity must be greater than zero.");
        Quantity = quantity;
        YieldPercent = RawMaterial.ValidYield(yieldPercent);
    }
}
