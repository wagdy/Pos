using Otantik.BuildingBlocks;

namespace OtantikPos.Inventory.Domain.Recipes;

// The kitchen's own grouping of its recipes (Sauces, Staff meals, Catering orders), apart from
// the menu's categories, which belong to the delivery system. Every recipe written on the recipe
// screen is in one. Its id is made by the till, so a retried save adds it once.
public sealed class RecipeCategory : AggregateRoot
{
    private RecipeCategory() { }

    public RecipeCategory(Guid id, string name, string? nameAr = null)
    {
        Id = id;
        CreatedAtUtc = DateTime.UtcNow;
        Rename(name, nameAr);
    }

    public string Name { get; private set; } = string.Empty;

    // The name as compared: capitals and extra spaces ignored, so "staff  meals" is Staff meals.
    public string NormalizedName { get; private set; } = string.Empty;

    public string? NameAr { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public void Rename(string name, string? nameAr = null)
    {
        var clean = Collapse(name ?? string.Empty);
        if (clean.Length == 0)
            throw new DomainException("Give the category a name.");
        var cleanAr = string.IsNullOrWhiteSpace(nameAr) ? null : Collapse(nameAr);
        if (clean.Length > 100 || cleanAr?.Length > 100)
            throw new DomainException("Keep the category's name under 100 characters.");

        Name = clean;
        NormalizedName = Normalize(clean);
        NameAr = cleanAr;
    }

    public static string Normalize(string name) => Collapse(name).ToUpperInvariant();

    private static string Collapse(string name) => string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

public interface IRecipeCategoryRepository : IRepository<RecipeCategory>
{
    Task<IReadOnlyList<RecipeCategory>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<RecipeCategory?> GetByNormalizedNameAsync(string normalizedName, CancellationToken cancellationToken = default);

    // Only once no active recipe is in it.
    void Remove(RecipeCategory category);
}
