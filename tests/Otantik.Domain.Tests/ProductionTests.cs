using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Domain.Production;
using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Inventory.Domain.StockMovements;

namespace Otantik.Domain.Tests;

// A prep item is made from other materials and kept in stock. A production order takes its
// ingredients off stock at their average cost and puts it in at their total over what came out,
// moving its average as a delivery moves a bought material's.
public class ProductionTests
{
    private static RawMaterial Bought(string name, UnitOfMeasure unit, decimal onHand, decimal? costPerKg)
    {
        var material = new RawMaterial(name, unit);
        if (onHand > 0)
            material.ReceivePurchase(onHand, Guid.NewGuid(), costPerKg is { } c ? c * onHand / 1000 : null);
        else if (costPerKg is { } cost)
            material.SetCost(cost);
        return material;
    }

    // 10 kg of dough: 6 kg of flour, 100 g of yeast, 3.5 L of water.
    private static (RawMaterial Dough, PrepRecipe Recipe, RawMaterial Flour, RawMaterial Yeast, RawMaterial Water) Dough(decimal flourOnHand = 20_000)
    {
        var flour = Bought("Flour", UnitOfMeasure.Gram, flourOnHand, 20);
        var yeast = Bought("Yeast", UnitOfMeasure.Gram, 1000, 500);
        var water = Bought("Water", UnitOfMeasure.Millilitre, 0, 0);
        var dough = new RawMaterial("Pizza dough", UnitOfMeasure.Gram, kind: MaterialKind.Prep);
        var recipe = new PrepRecipe(dough, 10_000);
        recipe.Replace([(flour, 6000m, null), (yeast, 100m, null), (water, 3500m, null)]);
        return (dough, recipe, flour, yeast, water);
    }

    private static Dictionary<Guid, RawMaterial> Stock(params RawMaterial[] materials) => materials.ToDictionary(m => m.Id);

    [Fact]
    public void A_batch_takes_its_ingredients_at_their_average_and_costs_out_over_what_came_out()
    {
        var (dough, recipe, flour, yeast, water) = Dough();

        // Planned 10 kg, 9.6 kg came out: 120 of flour and 50 of yeast over 9.6 kg.
        var (order, movements) = ProductionOrder.Make(Guid.NewGuid(), dough, recipe, Stock(flour, yeast, water), 10_000, 9_600, "Omar");

        Assert.Equal((170m, 9_600m), (order.TotalCost, dough.QuantityOnHand));
        Assert.Equal(170m / 9_600m, order.UnitCost);
        Assert.Equal(17.71m, Math.Round(dough.CostPerPurchaseUnit!.Value, 2));
        Assert.Equal((14_000m, 900m, -3_500m), (flour.QuantityOnHand, yeast.QuantityOnHand, water.QuantityOnHand));
        Assert.Equal(
            [(flour.Id, StockMovementReason.ProductionInput, -6_000m, 0.02m), (yeast.Id, StockMovementReason.ProductionInput, -100m, 0.5m),
             (water.Id, StockMovementReason.ProductionInput, -3_500m, 0m), (dough.Id, StockMovementReason.ProductionOutput, 9_600m, 170m / 9_600m)],
            movements.Select(m => (m.RawMaterialId, m.Reason, m.Quantity, m.UnitCost!.Value)));
        Assert.All(movements, m => Assert.Equal(order.Id, m.SourceId));
        Assert.Equal([(6_000m, 120m), (100m, 50m), (3_500m, 0m)], order.Lines.Select(l => (l.Quantity, l.Cost)));
    }

    [Fact]
    public void The_next_batch_moves_the_dough_average_as_a_delivery_would()
    {
        var (dough, recipe, flour, yeast, water) = Dough();
        ProductionOrder.Make(Guid.NewGuid(), dough, recipe, Stock(flour, yeast, water), 10_000, 9_600, "Omar");
        // Flour dearer now: 14 kg at 20 and 6 kg at 30 is 23 a kg.
        flour.ReceivePurchase(6_000, Guid.NewGuid(), 180);
        Assert.Equal(0.023m, flour.AverageCost);

        var (second, _) = ProductionOrder.Make(Guid.NewGuid(), dough, recipe, Stock(flour, yeast, water), 10_000, 10_000, "Sara");

        // 6 kg at 23 and the yeast: 188 for 10 kg. On the shelf, 170 and 188 over 19.6 kg.
        Assert.Equal((188m, 0.0188m), (second.TotalCost, second.UnitCost));
        Assert.Equal(19_600m, dough.QuantityOnHand);
        Assert.Equal(Math.Round(358m / 19_600m, 10), Math.Round(dough.AverageCost!.Value, 10));
    }

    [Fact]
    public void Ingredients_scale_with_what_is_planned_and_their_trim()
    {
        var onions = Bought("Onions", UnitOfMeasure.Gram, 10_000, 15);
        var sauce = new RawMaterial("House sauce", UnitOfMeasure.Millilitre, kind: MaterialKind.Prep);
        var recipe = new PrepRecipe(sauce, 5_000);
        // 850 g peeled at 85% takes 1 kg off the shelf for a 5 L batch.
        recipe.Replace([(onions, 850m, 85m)]);

        Assert.Equal([(onions.Id, 1_000m)], recipe.RequirementsFor(5_000));
        Assert.Equal([(onions.Id, 500m)], recipe.RequirementsFor(2_500));
        Assert.Equal([(onions.Id, 333.333m)], recipe.RequirementsFor(1_666.666m));
    }

    [Fact]
    public void Too_little_on_record_still_makes_the_batch_and_goes_below_zero()
    {
        var (dough, recipe, flour, yeast, water) = Dough(flourOnHand: 1_000);

        ProductionOrder.Make(Guid.NewGuid(), dough, recipe, Stock(flour, yeast, water), 10_000, 10_000, "Omar");

        Assert.Equal(-5_000m, flour.QuantityOnHand);
        Assert.Equal(10_000m, dough.QuantityOnHand);
    }

    [Fact]
    public void A_batch_with_an_ingredient_that_has_no_cost_is_not_made()
    {
        var (dough, recipe, flour, yeast, _) = Dough();
        var salt = new RawMaterial("Salt", UnitOfMeasure.Gram);
        recipe.Replace([(flour, 6000m, null), (yeast, 100m, null), (salt, 150m, null)]);

        var refused = Assert.Throws<DomainException>(() => ProductionOrder.Make(Guid.NewGuid(), dough, recipe, Stock(flour, yeast, salt), 10_000, 10_000, "Omar"));

        Assert.StartsWith("Salt has no cost yet", refused.Message);
        Assert.Equal((20_000m, 0m), (flour.QuantityOnHand, dough.QuantityOnHand));
    }

    [Fact]
    public void Made_and_bought_stay_apart()
    {
        var (dough, recipe, flour, yeast, water) = Dough();

        Assert.Throws<DomainException>(() => dough.ReceivePurchase(1_000, Guid.NewGuid(), 20));
        Assert.Throws<DomainException>(() => flour.ReceiveProduction(1_000, Guid.NewGuid(), 20));
        Assert.Throws<DomainException>(() => new PrepRecipe(flour, 1_000));
        Assert.Throws<DomainException>(() => recipe.Replace([(dough, 100m, null)]));
        Assert.Throws<DomainException>(() => recipe.Replace([(flour, 100m, null), (flour, 200m, null)]));
        Assert.Throws<DomainException>(() => ProductionOrder.Make(Guid.NewGuid(), dough, recipe, Stock(flour, yeast, water), 10_000, 0, "Omar"));
        Assert.Throws<DomainException>(() => ProductionOrder.Make(Guid.NewGuid(), dough, new PrepRecipe(dough, 1_000), Stock(), 1_000, 1_000, "Omar"));
    }
}
