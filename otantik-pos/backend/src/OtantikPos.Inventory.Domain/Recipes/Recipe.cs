using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Domain.RawMaterials;

namespace OtantikPos.Inventory.Domain.Recipes;

// What a catalog item is made of, as a recipe is written: each raw material's Edible Portion
// for the whole recipe and its yield, and how many portions that makes. One sold unit takes one
// portion's share off stock, as purchased. A catalog item with no recipe is simply not
// stock-tracked.
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

    // How many portions the quantities below make, as a recipe is written: 15 for a tray of
    // strata. A sold item uses one portion's share.
    public int Portions { get; private set; } = 1;

    public IReadOnlyCollection<RecipeIngredient> Ingredients => _ingredients.AsReadOnly();

    public void SetPortions(int portions) =>
        Portions = portions >= 1 ? portions : throw new DomainException("A recipe makes at least one portion.");

    // Adds the ingredient, or changes it if it is already there. Takes the material itself rather
    // than an id so that a recipe can never name one that does not exist. Without a yield, the
    // material's default is used.
    public void SetIngredient(RawMaterial material, decimal quantity, decimal? yieldPercent = null)
    {
        var existing = _ingredients.SingleOrDefault(i => i.RawMaterialId == material.Id);
        var yield = yieldPercent ?? existing?.YieldPercent ?? material.DefaultYieldPercent;
        if (existing is null)
            _ingredients.Add(new RecipeIngredient(Id, material.Id, quantity, yield));
        else
            existing.Change(quantity, yield);
    }

    public void RemoveIngredient(Guid rawMaterialId) =>
        _ingredients.RemoveAll(i => i.RawMaterialId == rawMaterialId);

    public IReadOnlyList<(Guid RawMaterialId, decimal Quantity)> RequirementsFor(int units)
    {
        if (units <= 0)
            throw new DomainException("Units must be at least 1.");

        return _ingredients.Select(i => (i.RawMaterialId, Quantity: i.AsPurchasedQuantity * units / Portions)).ToList();
    }
}
