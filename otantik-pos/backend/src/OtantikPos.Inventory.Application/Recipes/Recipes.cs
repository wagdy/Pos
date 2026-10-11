using MediatR;
using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Application.Common;
using OtantikPos.Inventory.Application.RawMaterials;
using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Inventory.Domain.Recipes;

namespace OtantikPos.Inventory.Application.Recipes;

// Quantity: the Edible Portion for the whole recipe, in the material's own unit. YieldPercent:
// null takes the material's default.
public sealed record RecipeIngredientDto(Guid RawMaterialId, decimal Quantity, decimal? YieldPercent = null);

public sealed record RecipeDto(
    Guid Id,
    string? Name,
    Guid? RecipeCategoryId,
    decimal? PlannedPrice,
    RecipeTargetKind? TargetKind,
    int? CatalogItemId,
    int? VariantId,
    IReadOnlyList<RecipeIngredientDto> Ingredients,
    int Portions,
    bool IsDeleted)
{
    public static RecipeDto From(Recipe recipe) => new(
        recipe.Id,
        recipe.Name,
        recipe.RecipeCategoryId,
        recipe.PlannedPrice,
        recipe.TargetKind,
        recipe.CatalogItemId,
        recipe.VariantId,
        recipe.Ingredients.Select(i => new RecipeIngredientDto(i.RawMaterialId, i.Quantity, i.YieldPercent)).ToList(),
        recipe.Portions,
        recipe.IsDeleted);
}

internal static class RecipeWriting
{
    // The ingredient list, whole: what is not on it goes.
    public static async Task WriteAsync(
        this Recipe recipe, IRawMaterialRepository materials, IReadOnlyList<RecipeIngredientDto> ingredients, int portions, CancellationToken cancellationToken)
    {
        var byId = await materials.GetRequiredByIdsAsync(ingredients.Select(i => i.RawMaterialId).ToList(), cancellationToken);
        foreach (var dropped in recipe.Ingredients.Where(i => !byId.ContainsKey(i.RawMaterialId)).ToList())
            recipe.RemoveIngredient(dropped.RawMaterialId);

        recipe.SetPortions(portions);
        foreach (var ingredient in ingredients)
            recipe.SetIngredient(byId[ingredient.RawMaterialId], ingredient.Quantity, ingredient.YieldPercent);
    }

    // One active recipe per menu item: the one it sells by.
    public static async Task EnsureFreeAsync(this IRecipeRepository recipes, RecipeTarget? target, Guid recipeId, CancellationToken cancellationToken)
    {
        if (target is { } t && await recipes.GetByTargetAsync(t, cancellationToken) is { } other && other.Id != recipeId)
            throw new DomainException(
                $"That menu item already sells by {(other.Name is { } name ? $"the recipe \"{name}\"" : "another recipe")}. Unlink or delete that one first.");
    }
}

// Replaces the whole recipe a menu item, one of its variants, or an add-on sells by, adding it if
// it has none. An empty list deletes it, and that target stops being stock-tracked.
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
    int Portions = 1,
    string StaffName = "") : IRequest<RecipeDto?>;

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
                recipe.Delete(request.StaffName);
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            return null;
        }

        if (recipe is null)
            recipes.Add(recipe = new Recipe(target));
        await recipe.WriteAsync(materials, request.Ingredients, request.Portions, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return RecipeDto.From(recipe);
    }
}

// Null when the target has no active recipe, which means it is not stock-tracked.
public sealed record GetRecipeQuery(RecipeTargetKind TargetKind, int CatalogItemId, int? VariantId) : IRequest<RecipeDto?>;

internal sealed class GetRecipeHandler(IRecipeRepository recipes) : IRequestHandler<GetRecipeQuery, RecipeDto?>
{
    public async Task<RecipeDto?> Handle(GetRecipeQuery request, CancellationToken cancellationToken)
    {
        var recipe = await recipes.GetByTargetAsync(new RecipeTarget(request.TargetKind, request.CatalogItemId, request.VariantId), cancellationToken);
        return recipe is null ? null : RecipeDto.From(recipe);
    }
}

// Adds a recipe (no id) or changes one: its name, its category, what it is linked to on the menu
// if anything, its portions and the whole ingredient list. Linked, the menu item sells by it from
// the next sale. The category is required here: a recipe written on the recipe screen is sorted.
public sealed record SaveRecipeCommand(
    Guid? RecipeId,
    string? Name,
    RecipeTargetKind? TargetKind,
    int? CatalogItemId,
    int? VariantId,
    int Portions,
    IReadOnlyList<RecipeIngredientDto> Ingredients,
    Guid? RecipeCategoryId = null,
    decimal? PlannedPrice = null) : IRequest<RecipeDto>;

internal sealed class SaveRecipeHandler(
    IRecipeRepository recipes, IRecipeCategoryRepository categories, IRawMaterialRepository materials, IUnitOfWork unitOfWork)
    : IRequestHandler<SaveRecipeCommand, RecipeDto>
{
    public async Task<RecipeDto> Handle(SaveRecipeCommand request, CancellationToken cancellationToken)
    {
        if (request.Ingredients.Count == 0)
            throw new DomainException("Add at least one ingredient.");
        if ((request.TargetKind is null) != (request.CatalogItemId is null))
            throw new DomainException("Choose the menu item to link it to, or none.");
        if (request.RecipeCategoryId is not { } categoryId)
            throw new DomainException("Choose the recipe's category.");
        if (await categories.GetByIdAsync(categoryId, cancellationToken) is null)
            throw new DomainException("That category is no longer there. Choose another.");
        RecipeTarget? target = request.TargetKind is { } kind && request.CatalogItemId is { } id ? new RecipeTarget(kind, id, request.VariantId) : null;

        Recipe recipe;
        if (request.RecipeId is { } recipeId)
        {
            recipe = await recipes.GetRequiredAsync(recipeId, cancellationToken);
            if (recipe.IsDeleted)
                throw new DomainException("This recipe is deleted. Restore it to change it.");
            await recipes.EnsureFreeAsync(target, recipe.Id, cancellationToken);
            recipe.Define(request.Name, categoryId, request.PlannedPrice, target);
        }
        else
        {
            recipe = new Recipe(request.Name, categoryId, request.PlannedPrice, target);
            await recipes.EnsureFreeAsync(target, recipe.Id, cancellationToken);
            recipes.Add(recipe);
        }

        await recipe.WriteAsync(materials, request.Ingredients, request.Portions, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return RecipeDto.From(recipe);
    }
}

// Marks it deleted: off the screens, and a menu item it was linked to stops taking stock by it.
// Deleting it again changes nothing.
public sealed record DeleteRecipeCommand(Guid RecipeId, string StaffName = "") : IRequest;

internal sealed class DeleteRecipeHandler(IRecipeRepository recipes, IUnitOfWork unitOfWork) : IRequestHandler<DeleteRecipeCommand>
{
    public async Task Handle(DeleteRecipeCommand request, CancellationToken cancellationToken)
    {
        var recipe = await recipes.GetRequiredAsync(request.RecipeId, cancellationToken);
        recipe.Delete(request.StaffName);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

// Back as it was, linked again if its menu item has no other recipe since. If it has, the recipe
// comes back off the menu, under its own name or the one given (the menu item's, from the screen).
public sealed record RestoreRecipeCommand(Guid RecipeId, string? Name = null) : IRequest<RecipeDto>;

internal sealed class RestoreRecipeHandler(IRecipeRepository recipes, IUnitOfWork unitOfWork) : IRequestHandler<RestoreRecipeCommand, RecipeDto>
{
    public async Task<RecipeDto> Handle(RestoreRecipeCommand request, CancellationToken cancellationToken)
    {
        var recipe = await recipes.GetRequiredAsync(request.RecipeId, cancellationToken);
        if (recipe.Target is { } target && await recipes.GetByTargetAsync(target, cancellationToken) is { } other && other.Id != recipe.Id)
            recipe.Unlink(recipe.Name ?? request.Name);
        recipe.Restore();
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return RecipeDto.From(recipe);
    }
}
