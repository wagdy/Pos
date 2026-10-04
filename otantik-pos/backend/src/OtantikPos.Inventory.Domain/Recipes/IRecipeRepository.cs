using Otantik.BuildingBlocks;

namespace OtantikPos.Inventory.Domain.Recipes;

// Returns recipes with their Ingredients loaded.
public interface IRecipeRepository : IRepository<Recipe>
{
    Task<Recipe?> GetByTargetAsync(RecipeTarget target, CancellationToken cancellationToken = default);

    // Every recipe a set of sold items could need, in one round trip (see
    // StockRequirements.TargetsFor). Targets with no recipe are simply absent.
    Task<IReadOnlyList<Recipe>> GetByTargetsAsync(IReadOnlyCollection<RecipeTarget> targets, CancellationToken cancellationToken = default);

    void Remove(Recipe recipe);
}
