using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Domain.RawMaterials;

namespace OtantikPos.Inventory.Domain.Production;

// A prep item's bill of materials, for one batch as the kitchen makes it: "10 kg of pizza dough
// takes 6 kg of flour, 3.5 L of water, 100 g of yeast". Its ingredients can be bought materials
// or other prep items. Its id is the prep item's, so each prep item has one.
public sealed class PrepRecipe : AggregateRoot
{
    private readonly List<PrepIngredient> _ingredients = [];

    private PrepRecipe() { }

    public PrepRecipe(RawMaterial prepItem, decimal batchQuantity)
    {
        if (!prepItem.IsPrepItem)
            throw new DomainException($"{prepItem.Name} is bought, not made: it has no recipe to make it from.");
        Id = prepItem.Id;
        SetBatch(batchQuantity);
    }

    public Guid PrepItemId => Id;

    // What one batch makes, in the prep item's own unit: 10,000 g.
    public decimal BatchQuantity { get; private set; }

    public IReadOnlyCollection<PrepIngredient> Ingredients => _ingredients.AsReadOnly();

    public void SetBatch(decimal batchQuantity) =>
        BatchQuantity = batchQuantity > 0 ? batchQuantity : throw new DomainException("A batch makes more than nothing.");

    // The whole list, replacing what was there. Each quantity is the Edible Portion for one batch,
    // in the ingredient's own unit, as a dish's recipe is written.
    public void Replace(IReadOnlyCollection<(RawMaterial Material, decimal Quantity, decimal? YieldPercent)> ingredients)
    {
        if (ingredients.Any(i => i.Material.Id == Id))
            throw new DomainException("A prep item cannot be made from itself.");
        if (ingredients.Select(i => i.Material.Id).Distinct().Count() != ingredients.Count)
            throw new DomainException("Each ingredient goes on the recipe once: add its quantities together.");

        var lines = ingredients
            .Select(i => new PrepIngredient(Id, i.Material.Id, i.Quantity, i.YieldPercent ?? i.Material.DefaultYieldPercent))
            .ToList();
        _ingredients.Clear();
        _ingredients.AddRange(lines);
    }

    // What `quantity` of the prep item takes from stock, as purchased, to the gram: the batch's
    // ingredients scaled to it.
    public IReadOnlyList<(Guid RawMaterialId, decimal Quantity)> RequirementsFor(decimal quantity)
    {
        if (quantity <= 0)
            throw new DomainException("Make more than nothing.");
        return _ingredients
            .Select(i => (i.RawMaterialId, Math.Round(i.AsPurchasedQuantity * quantity / BatchQuantity, 3, MidpointRounding.AwayFromZero)))
            .ToList();
    }
}

public sealed class PrepIngredient : Entity
{
    private PrepIngredient() { }

    internal PrepIngredient(Guid prepRecipeId, Guid rawMaterialId, decimal quantity, decimal yieldPercent)
    {
        if (quantity <= 0)
            throw new DomainException("Each ingredient needs a quantity greater than zero.");
        PrepRecipeId = prepRecipeId;
        RawMaterialId = rawMaterialId;
        Quantity = quantity;
        YieldPercent = RawMaterial.ValidYield(yieldPercent);
    }

    public Guid PrepRecipeId { get; private set; }
    public Guid RawMaterialId { get; private set; }

    // The Edible Portion for one batch, in the ingredient's own unit.
    public decimal Quantity { get; private set; }

    public decimal YieldPercent { get; private set; } = 100;

    // What comes off stock for one batch: 85 g of peeled onion at 85% takes 100 g.
    public decimal AsPurchasedQuantity => Quantity * 100 / YieldPercent;
}

public interface IPrepRecipeRepository : IRepository<PrepRecipe>
{
    Task<IReadOnlyList<PrepRecipe>> GetAllAsync(CancellationToken cancellationToken = default);
}
