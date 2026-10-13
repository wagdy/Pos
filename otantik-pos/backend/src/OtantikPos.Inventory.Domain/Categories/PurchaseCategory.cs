using Otantik.BuildingBlocks;

namespace OtantikPos.Inventory.Domain.Categories;

// Where an expense filed under the category counts in the profit and loss. Stock bought under it
// counts nowhere here: it is cost of sales once the recipes use it.
public enum ExpenseLine
{
    // Utilities, cleaning, maintenance, marketing: costs the manager can bring down.
    Controllable = 1,

    // Rent and what comes with the premises.
    Occupation = 2,

    // Other costs that do not move with sales: insurance, licences, loan repayments.
    OtherFixed = 3,

    // Salaries and what comes with them, paid as expenses (a voucher from the safe): added to the
    // month's wages and benefits.
    Wages = 4,
}

// What the restaurant buys, grouped (Meat, Dairy, Packaging, Cleaning, Utilities): every raw
// material is in one, and so is every expense item, so purchases can be added up by category.
// Its id is made by the till, so a retried save adds it once. General is always there, for
// anything not yet sorted, and cannot be deleted.
public sealed class PurchaseCategory : AggregateRoot
{
    public static readonly Guid GeneralId = new("3f3f4d50-4158-44c8-bb35-e3dd347717ea");
    public const string GeneralName = "General";
    public const string GeneralNameAr = "عام";

    private PurchaseCategory() { }

    public PurchaseCategory(Guid id, string name, string? nameAr = null, ExpenseLine expenseLine = ExpenseLine.Controllable)
    {
        Id = id;
        CreatedAtUtc = DateTime.UtcNow;
        Update(name, nameAr, expenseLine);
    }

    public static PurchaseCategory General() => new(GeneralId, GeneralName, GeneralNameAr);

    public string Name { get; private set; } = string.Empty;

    // The name as compared: capitals and extra spaces ignored, so "dry  goods" is Dry goods.
    public string NormalizedName { get; private set; } = string.Empty;

    public string? NameAr { get; private set; }
    public ExpenseLine ExpenseLine { get; private set; } = ExpenseLine.Controllable;
    public DateTime CreatedAtUtc { get; private set; }

    public bool IsGeneral => Id == GeneralId;

    public void Update(string name, string? nameAr, ExpenseLine expenseLine)
    {
        var clean = Collapse(name ?? string.Empty);
        if (clean.Length == 0)
            throw new DomainException("Give the category a name.");
        var cleanAr = string.IsNullOrWhiteSpace(nameAr) ? null : Collapse(nameAr);
        if (clean.Length > 100 || cleanAr?.Length > 100)
            throw new DomainException("Keep the category's name under 100 characters.");
        if (!Enum.IsDefined(expenseLine))
            throw new DomainException("Choose where its expenses count.");

        Name = clean;
        NormalizedName = Normalize(clean);
        NameAr = cleanAr;
        ExpenseLine = expenseLine;
    }

    public static string Normalize(string name) => Collapse(name).ToUpperInvariant();

    private static string Collapse(string name) => string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

// How many raw materials and expense items are filed under a category.
public sealed record PurchaseCategoryUse(int Materials, int ExpenseItems);

public interface IPurchaseCategoryRepository : IRepository<PurchaseCategory>
{
    Task<IReadOnlyList<PurchaseCategory>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<PurchaseCategory?> GetByNormalizedNameAsync(string normalizedName, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, PurchaseCategoryUse>> GetUseAsync(CancellationToken cancellationToken = default);

    // Only once nothing is filed under it, and never General.
    void Remove(PurchaseCategory category);
}
