using MediatR;
using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Application.RawMaterials;
using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Inventory.Domain.Recipes;

namespace OtantikPos.Inventory.Application.Recipes;

// Quantity: the Edible Portion for the whole recipe, in the material's own unit. YieldPercent:
// null takes the material's default.
public sealed record RecipeIngredientDto(Guid RawMaterialId, decimal Quantity, decimal? YieldPercent = null);

public sealed record RecipeDto(
    RecipeTargetKind TargetKind, int CatalogItemId, int? VariantId, IReadOnlyList<RecipeIngredientDto> Ingredients, int Portions = 1)
{
    public static RecipeDto From(Recipe recipe) => new(
        recipe.TargetKind,
        recipe.CatalogItemId,
        recipe.VariantId,
        recipe.Ingredients.Select(i => new RecipeIngredientDto(i.RawMaterialId, i.Quantity, i.YieldPercent)).ToList(),
        recipe.Portions);
}

// Replaces the whole recipe of a menu item, one of its variants, or an add-on. An empty list
// removes it, and that target stops being stock-tracked.
//
// The catalog ids are not checked against the menu: Inventory has no access to it. The screen
// picks them from the menu the till already shows. A wrong id makes a recipe that never
// matches a sale, and does no other harm.
//
// A change applies from the next sale on. A Void After returns the quantities as they are at
// the time of the void, even for a dish sold under the old recipe.
public sealed record SetRecipeCommand(
    RecipeTargetKind TargetKind,
    int CatalogItemId,
    int? VariantId,
    IReadOnlyList<RecipeIngredientDto> Ingredients,
    int Portions = 1) : IRequest<RecipeDto?>;

internal sealed class SetRecipeHandler(IRecipeRepository recipes, IRawMaterialRepository materials, IUnitOfWork unitOfWork)
    : IRequestHandler<SetRecipeCommand, RecipeDto?>
{
    public async Task<RecipeDto?> Handle(SetRecipeCommand request, CancellationToken cancellationToken)
    {
        var target = new RecipeTarget(request.TargetKind, request.CatalogItemId, request.VariantId);
        var recipe = await recipes.GetByTargetAsync(target, cancellationToken);

        if (request.Ingredients.Count == 0)
        {
            if (recipe is not null)
            {
                recipes.Remove(recipe);
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            return null;
        }

        var byId = await materials.GetRequiredByIdsAsync(request.Ingredients.Select(i => i.RawMaterialId).ToList(), cancellationToken);

        if (recipe is null)
        {
            recipe = new Recipe(target);
            recipes.Add(recipe);
        }

        foreach (var dropped in recipe.Ingredients.Where(i => !byId.ContainsKey(i.RawMaterialId)).ToList())
            recipe.RemoveIngredient(dropped.RawMaterialId);

        recipe.SetPortions(request.Portions);
        foreach (var ingredient in request.Ingredients)
            recipe.SetIngredient(byId[ingredient.RawMaterialId], ingredient.Quantity, ingredient.YieldPercent);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return RecipeDto.From(recipe);
    }
}

// Null when the target has no recipe, which means it is not stock-tracked.
public sealed record GetRecipeQuery(RecipeTargetKind TargetKind, int CatalogItemId, int? VariantId) : IRequest<RecipeDto?>;

internal sealed class GetRecipeHandler(IRecipeRepository recipes) : IRequestHandler<GetRecipeQuery, RecipeDto?>
{
    public async Task<RecipeDto?> Handle(GetRecipeQuery request, CancellationToken cancellationToken)
    {
        var recipe = await recipes.GetByTargetAsync(new RecipeTarget(request.TargetKind, request.CatalogItemId, request.VariantId), cancellationToken);
        return recipe is null ? null : RecipeDto.From(recipe);
    }
}
