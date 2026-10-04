using Otantik.BuildingBlocks;

namespace OtantikPos.Inventory.Domain.Recipes;

public sealed class RecipeIngredient : Entity
{
    private RecipeIngredient() { }

    internal RecipeIngredient(Guid recipeId, Guid rawMaterialId, decimal quantityPerPortion)
    {
        RecipeId = recipeId;
        RawMaterialId = rawMaterialId;
        QuantityPerPortion = quantityPerPortion;
    }

    public Guid RecipeId { get; private set; }

    public Guid RawMaterialId { get; private set; }

    // Per unit of the recipe's target, in the raw material's own unit: grams, millilitres
    // or pieces.
    public decimal QuantityPerPortion { get; private set; }

    internal void ChangeQuantity(decimal quantityPerPortion) => QuantityPerPortion = quantityPerPortion;
}
