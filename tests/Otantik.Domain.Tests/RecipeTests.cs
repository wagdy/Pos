using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Inventory.Domain.Recipes;

namespace Otantik.Domain.Tests;

// A recipe on the menu sells by it; one off the menu (R&D) is costed, never sold. Deleted, it is
// marked so and kept, and no sale takes stock by it.
public class RecipeTests
{
    private const int Burger = 6;
    private readonly RawMaterial _patty = new("Beef patty", UnitOfMeasure.Piece);

    [Fact]
    public void An_off_menu_recipe_needs_a_name_and_a_linked_one_can_go_by_its_menu_item()
    {
        Assert.Throws<DomainException>(() => new Recipe(" ", null, null, null));
        var mains = Guid.NewGuid();
        var trial = new Recipe(" Truffle burger ", mains, 180, null);
        Assert.Equal(("Truffle burger", (Guid?)mains, 180m, false, (RecipeTarget?)null), (trial.Name, trial.RecipeCategoryId, trial.PlannedPrice, trial.IsLinked, trial.Target));

        var linked = new Recipe(RecipeTarget.ForMenuItem(Burger));
        Assert.Equal(((string?)null, true, RecipeTarget.ForMenuItem(Burger)), (linked.Name, linked.IsLinked, linked.Target!.Value));

        // Unlinked, it needs a name of its own.
        Assert.Throws<DomainException>(() => linked.Unlink());
        linked.Unlink("Old burger");
        Assert.Equal(("Old burger", false), (linked.Name, linked.IsLinked));

        Assert.Throws<DomainException>(() => new Recipe("Garlic", null, null, new RecipeTarget(RecipeTargetKind.AddOn, 7, 1)));
        Assert.Throws<DomainException>(() => new Recipe("Free", null, -1, null));
    }

    [Fact]
    public void A_deleted_recipe_is_kept_and_marked_and_can_come_back()
    {
        var recipe = new Recipe(RecipeTarget.ForMenuItem(Burger));

        recipe.Delete("Omar");
        var deletedAt = recipe.DeletedAtUtc;
        recipe.Delete("Sara");
        Assert.Equal((true, "Omar", deletedAt), (recipe.IsDeleted, recipe.DeletedBy, recipe.DeletedAtUtc));

        recipe.Restore();
        Assert.Equal((false, (string?)null), (recipe.IsDeleted, recipe.DeletedBy));
    }

    [Fact]
    public void Only_an_active_recipe_on_the_menu_takes_stock()
    {
        var deleted = new Recipe(RecipeTarget.ForMenuItem(Burger));
        deleted.SetIngredient(_patty, 2);
        deleted.Delete("Omar");
        var offMenu = new Recipe("Burger trial", null, null, null);
        offMenu.SetIngredient(_patty, 3);
        var sold = new Recipe(RecipeTarget.ForMenuItem(Burger));
        sold.SetIngredient(_patty, 1);

        var needs = StockRequirements.For(new SoldItem(Burger, null, [], 2), [deleted, offMenu, sold]);

        Assert.Equal(2m, Assert.Single(needs).Value);
        Assert.Empty(StockRequirements.For(new SoldItem(Burger, null, [], 2), [deleted, offMenu]));
    }

    [Fact]
    public void A_category_is_named_tidily_and_compared_loosely()
    {
        var category = new RecipeCategory(Guid.NewGuid(), "  Staff   meals ", " وجبات  العاملين ");

        Assert.Equal(("Staff meals", "STAFF MEALS", "وجبات العاملين"), (category.Name, category.NormalizedName, category.NameAr));
        Assert.Equal(category.NormalizedName, RecipeCategory.Normalize("staff meals"));
        Assert.Throws<DomainException>(() => new RecipeCategory(Guid.NewGuid(), "  "));
        Assert.Throws<DomainException>(() => category.Rename(new string('x', 101)));

        var recipe = new Recipe("Family meal", category.Id, null, null);
        recipe.MoveTo(null);
        Assert.Null(recipe.RecipeCategoryId);
    }
}
