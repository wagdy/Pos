using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Domain.RawMaterials;

namespace OtantikPos.Inventory.Domain.Recipes;

// How much of each raw material one unit of a catalog item uses. A catalog item with no recipe
// is simply not stock-tracked.
public sealed class Recipe : AggregateRoot
{
    private readonly List<RecipeIngredient> _ingredients = [];

    private Recipe() { }

    public Recipe(RecipeTarget target)
    {
        if (target.Kind == RecipeTargetKind.AddOn && target.VariantId is not null)
            throw new DomainException("An add-on has no variants.");

        TargetKind = target.Kind;
        CatalogItemId = target.CatalogItemId;
        VariantId = target.VariantId;
    }

    // RecipeTarget's parts, as separate columns. One recipe per target.
    public RecipeTargetKind TargetKind { get; private set; }
    public int CatalogItemId { get; private set; }
    public int? VariantId { get; private set; }

    public RecipeTarget Target => new(TargetKind, CatalogItemId, VariantId);

    public IReadOnlyCollection<RecipeIngredient> Ingredients => _ingredients.AsReadOnly();

    // Adds the ingredient, or changes its quantity if it is already there. Takes the material
    // itself rather than an id so that a recipe can never name one that does not exist.
    public void SetIngredient(RawMaterial material, decimal quantityPerUnit)
    {
        if (quantityPerUnit <= 0)
            throw new DomainException("Ingredient quantity must be greater than zero.");

        var existing = _ingredients.SingleOrDefault(i => i.RawMaterialId == material.Id);
        if (existing is null)
            _ingredients.Add(new RecipeIngredient(Id, material.Id, quantityPerUnit));
        else
            existing.ChangeQuantity(quantityPerUnit);
    }

    public void RemoveIngredient(Guid rawMaterialId) =>
        _ingredients.RemoveAll(i => i.RawMaterialId == rawMaterialId);

    public IReadOnlyList<(Guid RawMaterialId, decimal Quantity)> RequirementsFor(int units)
    {
        if (units <= 0)
            throw new DomainException("Units must be at least 1.");

        return _ingredients.Select(i => (i.RawMaterialId, Quantity: i.QuantityPerPortion * units)).ToList();
    }
}
