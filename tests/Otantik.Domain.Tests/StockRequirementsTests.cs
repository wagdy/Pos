using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Inventory.Domain.Recipes;

namespace Otantik.Domain.Tests;

public class StockRequirementsTests
{
    private const int Shawarma = 10;
    private const int KiloTray = 101;
    private const int HalfTray = 102;
    private const int ExtraGarlic = 7;

    private readonly RawMaterial _chicken = new("Chicken", UnitOfMeasure.Gram);
    private readonly RawMaterial _garlicSauce = new("Garlic sauce", UnitOfMeasure.Millilitre);
    private readonly RawMaterial _bread = new("Bread", UnitOfMeasure.Piece);

    private IReadOnlyCollection<Recipe> Recipes()
    {
        var baseRecipe = new Recipe(RecipeTarget.ForMenuItem(Shawarma));
        baseRecipe.SetIngredient(_chicken, 150);
        baseRecipe.SetIngredient(_bread, 1);

        var kilo = new Recipe(RecipeTarget.ForVariant(Shawarma, KiloTray));
        kilo.SetIngredient(_chicken, 1000);

        var garlic = new Recipe(RecipeTarget.ForAddOn(ExtraGarlic));
        garlic.SetIngredient(_garlicSauce, 30);

        return [baseRecipe, kilo, garlic];
    }

    [Fact]
    public void Base_recipe_times_quantity()
    {
        var needs = StockRequirements.For(new SoldItem(Shawarma, null, [], 3), Recipes());

        Assert.Equal(450m, needs[_chicken.Id]);
        Assert.Equal(3m, needs[_bread.Id]);
    }

    // The variant's recipe replaces the base: a kilo tray is not a sandwich plus a tray.
    [Fact]
    public void A_variant_with_its_own_recipe_replaces_the_base()
    {
        var needs = StockRequirements.For(new SoldItem(Shawarma, KiloTray, [], 2), Recipes());

        Assert.Equal(2000m, needs[_chicken.Id]);
        Assert.False(needs.ContainsKey(_bread.Id));
    }

    [Fact]
    public void A_variant_without_its_own_recipe_uses_the_base()
    {
        var needs = StockRequirements.For(new SoldItem(Shawarma, HalfTray, [], 1), Recipes());

        Assert.Equal(150m, needs[_chicken.Id]);
    }

    [Fact]
    public void Add_ons_draw_stock_per_unit_like_their_price()
    {
        var needs = StockRequirements.For(new SoldItem(Shawarma, null, [ExtraGarlic], 2), Recipes());

        Assert.Equal(60m, needs[_garlicSauce.Id]);
        Assert.Equal(300m, needs[_chicken.Id]);
    }

    [Fact]
    public void An_item_with_no_recipe_draws_nothing()
    {
        Assert.Empty(StockRequirements.For(new SoldItem(999, null, [], 1), Recipes()));
    }

    [Fact]
    public void Targets_cover_base_variant_and_add_ons()
    {
        var targets = StockRequirements.TargetsFor([new SoldItem(Shawarma, KiloTray, [ExtraGarlic], 1)]);

        Assert.Equal(3, targets.Count);
        Assert.Contains(RecipeTarget.ForMenuItem(Shawarma), targets);
        Assert.Contains(RecipeTarget.ForVariant(Shawarma, KiloTray), targets);
        Assert.Contains(RecipeTarget.ForAddOn(ExtraGarlic), targets);
    }
}
