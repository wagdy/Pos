using Otantik.BuildingBlocks;

namespace OtantikPos.Inventory.Domain.Recipes;

// Returns recipes with their Ingredients loaded. Nothing removes one: a deleted recipe is marked so.
public interface IRecipeRepository : IRepository<Recipe>
{
    // The active recipe linked to the target, if any.
    Task<Recipe?> GetByTargetAsync(RecipeTarget target, CancellationToken cancellationToken = default);

    // Every active recipe a set of sold items could need, in one round trip (see
    // StockRequirements.TargetsFor). Targets with no recipe are simply absent.
    Task<IReadOnlyList<Recipe>> GetByTargetsAsync(IReadOnlyCollection<RecipeTarget> targets, CancellationToken cancellationToken = default);

    // Every recipe in the category, the deleted ones too: what moves with it when it goes.
    Task<IReadOnlyList<Recipe>> GetInCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default);

    // How many active recipes each category holds.
    Task<IReadOnlyDictionary<Guid, int>> CountByCategoryAsync(CancellationToken cancellationToken = default);
}
