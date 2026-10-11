using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Domain.RawMaterials;

namespace OtantikPos.Inventory.Domain.Recipes;

// A dish as the kitchen writes it: each raw material's Edible Portion for the whole recipe and
// its yield, and how many portions that makes. Linked to a catalog item (a menu item, one of its
// sizes, or an add-on), one sold unit takes one portion's share off stock, as purchased. Not
// linked, it is an off-menu or R&D recipe: costed, never sold. A catalog item with no linked
// recipe is simply not stock-tracked.
//
// Deleted, it is only marked so: it leaves the screens and the sales, and stays for the record of
// what the kitchen worked to. Each sale's stock movements already keep what it took, and at what
// cost, so past reports never depend on it.
public sealed class Recipe : AggregateRoot
{
    private readonly List<RecipeIngredient> _ingredients = [];

    private Recipe() { }

    // The recipe a catalog item sells by, under its name.
    public Recipe(RecipeTarget target)
        : this(null, null, null, target)
    {
    }

    public Recipe(string? name, Guid? categoryId, decimal? plannedPrice, RecipeTarget? target)
    {
        CreatedAtUtc = DateTime.UtcNow;
        Define(name, categoryId, plannedPrice, target);
    }

    // Its own name: required off the menu; on it, optional, the catalog item's name standing in.
    public string? Name { get; private set; }

    // The kitchen's own grouping (sauces, staff meals, catering), apart from the menu's categories.
    // Null for one not sorted yet: written before categories, or by its dish alone.
    public Guid? RecipeCategoryId { get; private set; }

    // What an off-menu recipe is meant to sell at, for its food cost before it is on the menu. A
    // linked recipe is costed against the menu's price.
    public decimal? PlannedPrice { get; private set; }

    // RecipeTarget's parts, as separate columns; all null for an off-menu recipe. One active
    // recipe per target.
    public RecipeTargetKind? TargetKind { get; private set; }
    public int? CatalogItemId { get; private set; }
    public int? VariantId { get; private set; }

    public RecipeTarget? Target => TargetKind is { } kind && CatalogItemId is { } id ? new RecipeTarget(kind, id, VariantId) : null;
    public bool IsLinked => CatalogItemId is not null;

    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? DeletedAtUtc { get; private set; }
    public string? DeletedBy { get; private set; }
    public bool IsDeleted => DeletedAtUtc is not null;

    // What it is called and what it is for, set together so the whole is checked: off the menu, it
    // needs a name of its own.
    public void Define(string? name, Guid? categoryId, decimal? plannedPrice, RecipeTarget? target)
    {
        var cleanName = Clean(name);
        if (cleanName?.Length > 200)
            throw new DomainException("Keep the recipe's name under 200 characters.");
        if (plannedPrice < 0)
            throw new DomainException("A planned price cannot be negative.");
        if (target is { Kind: RecipeTargetKind.AddOn, VariantId: not null })
            throw new DomainException("An add-on has no variants.");
        if (target is null && cleanName is null)
            throw new DomainException("Name the recipe: off the menu, it has no menu item's name to go by.");

        Name = cleanName;
        RecipeCategoryId = categoryId;
        PlannedPrice = plannedPrice;
        TargetKind = target?.Kind;
        CatalogItemId = target?.CatalogItemId;
        VariantId = target?.VariantId;
    }

    // Off the menu: kept, with its name, for the record. A sale no longer takes stock by it.
    public void Unlink(string? name = null) => Define(name ?? Name, RecipeCategoryId, PlannedPrice, null);

    // Into another category: its own being deleted.
    public void MoveTo(Guid? categoryId) => RecipeCategoryId = categoryId;

    public void Delete(string deletedBy)
    {
        if (IsDeleted)
            return;
        DeletedAtUtc = DateTime.UtcNow;
        DeletedBy = deletedBy;
    }

    public void Restore()
    {
        DeletedAtUtc = null;
        DeletedBy = null;
    }

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

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public IReadOnlyList<(Guid RawMaterialId, decimal Quantity)> RequirementsFor(int units)
    {
        if (units <= 0)
            throw new DomainException("Units must be at least 1.");

        return _ingredients.Select(i => (i.RawMaterialId, Quantity: i.AsPurchasedQuantity * units / Portions)).ToList();
    }
}
