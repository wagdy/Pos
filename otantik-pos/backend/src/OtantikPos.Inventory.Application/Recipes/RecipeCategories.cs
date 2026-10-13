using MediatR;
using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Application.Common;
using OtantikPos.Inventory.Domain.Production;
using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Inventory.Domain.Recipes;

namespace OtantikPos.Inventory.Application.Recipes;

// The kitchen's recipe categories, its own and not the menu's: the dishes' recipes and the prep
// items' are sorted under them alike. CategoryId is made by the till.

// Recipes: how many active dish recipes are in it; PrepItems, how many prep items still in use.
public sealed record RecipeCategoryDto(Guid Id, string Name, string? NameAr, int Recipes, int PrepItems = 0)
{
    public static RecipeCategoryDto From(RecipeCategory category, int recipes, int prepItems) =>
        new(category.Id, category.Name, category.NameAr, recipes, prepItems);
}

internal static class PrepItemsInCategories
{
    // How many prep items still in use are in each recipe category.
    public static async Task<IReadOnlyDictionary<Guid, int>> CountAsync(
        IPrepRecipeRepository prepRecipes, IRawMaterialRepository materials, CancellationToken cancellationToken)
    {
        var deleted = (await materials.GetAllAsync(cancellationToken)).Where(m => m.IsArchived).Select(m => m.Id).ToHashSet();
        return (await prepRecipes.GetAllAsync(cancellationToken))
            .Where(r => r.RecipeCategoryId is not null && !deleted.Contains(r.PrepItemId))
            .GroupBy(r => r.RecipeCategoryId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());
    }
}

// By name.
public sealed record GetRecipeCategoriesQuery : IRequest<IReadOnlyList<RecipeCategoryDto>>;

internal sealed class GetRecipeCategoriesHandler(
    IRecipeCategoryRepository categories, IRecipeRepository recipes, IPrepRecipeRepository prepRecipes, IRawMaterialRepository materials)
    : IRequestHandler<GetRecipeCategoriesQuery, IReadOnlyList<RecipeCategoryDto>>
{
    public async Task<IReadOnlyList<RecipeCategoryDto>> Handle(GetRecipeCategoriesQuery request, CancellationToken cancellationToken)
    {
        var counts = await recipes.CountByCategoryAsync(cancellationToken);
        var prepCounts = await PrepItemsInCategories.CountAsync(prepRecipes, materials, cancellationToken);
        return (await categories.GetAllAsync(cancellationToken))
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => RecipeCategoryDto.From(c, counts.GetValueOrDefault(c.Id), prepCounts.GetValueOrDefault(c.Id)))
            .ToList();
    }
}

// Adds the category, or renames it. Two cannot share a name: the recipes table could not tell them apart.
public sealed record SaveRecipeCategoryCommand(Guid CategoryId, string Name, string? NameAr = null) : IRequest<RecipeCategoryDto>;

internal sealed class SaveRecipeCategoryHandler(
    IRecipeCategoryRepository categories, IRecipeRepository recipes, IPrepRecipeRepository prepRecipes, IRawMaterialRepository materials,
    IUnitOfWork unitOfWork)
    : IRequestHandler<SaveRecipeCategoryCommand, RecipeCategoryDto>
{
    public async Task<RecipeCategoryDto> Handle(SaveRecipeCategoryCommand request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.Name)
            && await categories.GetByNormalizedNameAsync(RecipeCategory.Normalize(request.Name), cancellationToken) is { } namesake
            && namesake.Id != request.CategoryId)
            throw new DomainException($"There is already a category called {namesake.Name}.");

        var category = await categories.GetByIdAsync(request.CategoryId, cancellationToken);
        if (category is null)
            categories.Add(category = new RecipeCategory(request.CategoryId, request.Name, request.NameAr));
        else
            category.Rename(request.Name, request.NameAr);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return RecipeCategoryDto.From(
            category,
            (await recipes.CountByCategoryAsync(cancellationToken)).GetValueOrDefault(category.Id),
            (await PrepItemsInCategories.CountAsync(prepRecipes, materials, cancellationToken)).GetValueOrDefault(category.Id));
    }
}

// Deletes the category. Its recipes and prep items move first to MoveToId: one with active recipes
// or prep items in use is not deleted without somewhere for them to go. Deleted ones in it move
// with them, or are left uncategorized.
public sealed record DeleteRecipeCategoryCommand(Guid CategoryId, Guid? MoveToId = null) : IRequest;

internal sealed class DeleteRecipeCategoryHandler(
    IRecipeCategoryRepository categories, IRecipeRepository recipes, IPrepRecipeRepository prepRecipes, IRawMaterialRepository materials,
    IUnitOfWork unitOfWork)
    : IRequestHandler<DeleteRecipeCategoryCommand>
{
    public async Task Handle(DeleteRecipeCategoryCommand request, CancellationToken cancellationToken)
    {
        // Already gone: a retry of a delete that worked.
        if (await categories.GetByIdAsync(request.CategoryId, cancellationToken) is not { } category)
            return;

        var inIt = await recipes.GetInCategoryAsync(category.Id, cancellationToken);
        var prepInIt = await prepRecipes.GetInCategoryAsync(category.Id, cancellationToken);
        var deleted = (await materials.GetAllAsync(cancellationToken)).Where(m => m.IsArchived).Select(m => m.Id).ToHashSet();
        var active = inIt.Count(r => !r.IsDeleted) + prepInIt.Count(r => !deleted.Contains(r.PrepItemId));
        if (request.MoveToId is { } moveToId)
        {
            if (moveToId == category.Id)
                throw new DomainException("Choose another category for its recipes.");
            _ = await categories.GetRequiredAsync(moveToId, cancellationToken);
        }
        else if (active > 0)
        {
            throw new DomainException(
                $"{category.Name} has {active} {(active == 1 ? "recipe" : "recipes")}. Choose the category {(active == 1 ? "it moves" : "they move")} to.");
        }

        foreach (var recipe in inIt)
            recipe.MoveTo(request.MoveToId);
        foreach (var prepRecipe in prepInIt)
            prepRecipe.MoveTo(request.MoveToId);
        categories.Remove(category);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
