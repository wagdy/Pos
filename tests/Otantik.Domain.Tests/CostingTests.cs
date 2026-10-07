using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Inventory.Domain.Recipes;
using OtantikPos.Inventory.Domain.StockMovements;

namespace Otantik.Domain.Tests;

// What raw materials cost, and what a sale takes off stock once recipes have yields and
// portions: the ground under the Recipe Costing Template and the Monthly Theoretical Cost.
public class CostingTests
{
    private const int Strata = 20;

    // Bought by the kg, counted in grams: 10 kg at 300 then 10 kg at 360 average 330 a kg,
    // weighted by quantity.
    [Fact]
    public void Purchases_with_prices_keep_a_weighted_average_cost()
    {
        var beef = new RawMaterial("Minced beef", UnitOfMeasure.Gram);
        Assert.Equal(("kg", 1000m), (beef.PurchaseUnit, beef.PurchaseUnitSize));
        Assert.Null(beef.AverageCost);

        var first = beef.ReceivePurchase(10_000, Guid.NewGuid(), lineCost: 3_000);
        Assert.Equal(300m, beef.CostPerPurchaseUnit);
        Assert.Equal(0.3m, first.UnitCost);

        beef.ReceivePurchase(10_000, Guid.NewGuid(), lineCost: 3_600);
        Assert.Equal(330m, beef.CostPerPurchaseUnit);

        // Half used, then 10 kg more at 390: (10 kg × 330 + 10 kg × 390) ÷ 20 kg.
        beef.RecordSale(10_000, Guid.NewGuid());
        beef.ReceivePurchase(10_000, Guid.NewGuid(), lineCost: 3_900);
        Assert.Equal(360m, beef.CostPerPurchaseUnit);

        // A delivery with no price moves stock, not the cost.
        beef.ReceivePurchase(5_000, Guid.NewGuid());
        Assert.Equal(360m, beef.CostPerPurchaseUnit);
    }

    // Stock below zero is nothing to average with: the new price stands alone.
    [Fact]
    public void After_stock_went_negative_the_next_price_is_the_cost()
    {
        var oil = new RawMaterial("Frying oil", UnitOfMeasure.Millilitre);
        oil.SetCost(80);
        oil.RecordSale(500, Guid.NewGuid());

        oil.ReceivePurchase(5_000, Guid.NewGuid(), lineCost: 500);

        Assert.Equal(100m, oil.CostPerPurchaseUnit);
    }

    // Each movement keeps the cost of its moment, so a month's food cost does not change when
    // later prices do.
    [Fact]
    public void Every_movement_records_the_cost_at_its_moment()
    {
        var eggs = new RawMaterial("Eggs", UnitOfMeasure.Piece);
        eggs.SetPurchaseUnit("carton of 30", 30);
        eggs.SetCost(150);

        var sale = eggs.RecordSale(2, Guid.NewGuid());
        eggs.SetCost(180);

        Assert.Equal(5m, sale.UnitCost);
        Assert.Equal(StockMovementReason.Sale, sale.Reason);
        Assert.Equal(6m, eggs.RecordWaste(1, Guid.NewGuid()).UnitCost);
    }

    // A recipe is written for its portions in Edible Portion; a sale takes one portion's share
    // As Purchased: EP ÷ yield ÷ portions.
    [Fact]
    public void A_sale_takes_one_portion_as_purchased()
    {
        var peppers = new RawMaterial("Red bell peppers", UnitOfMeasure.Gram);
        peppers.SetDefaultYield(85);
        var eggs = new RawMaterial("Eggs", UnitOfMeasure.Piece);
        var recipe = new Recipe(RecipeTarget.ForMenuItem(Strata));
        recipe.SetPortions(15);
        recipe.SetIngredient(peppers, 255);
        recipe.SetIngredient(eggs, 12, yieldPercent: 98);

        var needs = StockRequirements.For(new SoldItem(Strata, null, [], 3), [recipe]);

        Assert.Equal(85m, recipe.Ingredients.Single(i => i.RawMaterialId == peppers.Id).YieldPercent);
        Assert.Equal(60m, needs[peppers.Id]);                      // 255 ÷ 0.85 = 300 g for 15, 20 g each
        Assert.Equal(12m / 0.98m / 15 * 3, needs[eggs.Id], 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void A_yield_is_more_than_nothing_and_at_most_everything(int percent)
    {
        var flour = new RawMaterial("Flour", UnitOfMeasure.Gram);
        Assert.Throws<DomainException>(() => flour.SetDefaultYield(percent));
        Assert.Throws<DomainException>(() => new Recipe(RecipeTarget.ForMenuItem(Strata)).SetIngredient(flour, 100, percent));
    }

    [Fact]
    public void A_recipe_makes_at_least_one_portion() =>
        Assert.Throws<DomainException>(() => new Recipe(RecipeTarget.ForMenuItem(Strata)).SetPortions(0));
}
