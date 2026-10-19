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

// What a category files: raw materials (stock: bought, counted, used in recipes) or general
// expenses (bought, never stock). A raw material goes in one of the first, an expense item in one
// of the second.
public enum CategoryKind
{
    RawMaterials = 1,
    Expenses = 2,
}

// What the restaurant buys, grouped (Meat, Dairy, Packaging for raw materials; Cleaning,
// Utilities, Rent for expenses): every raw material is in one of its kind, and so is every
// expense item, so purchases can be added up by category. Its id is made by the till, so a
// retried save adds it once. Each kind has its General, for anything not yet sorted: always
// there, never deleted, and never the other kind.
public sealed class PurchaseCategory : AggregateRoot
{
    public static readonly Guid GeneralId = new("3f3f4d50-4158-44c8-bb35-e3dd347717ea");
    public const string GeneralName = "General";
    public const string GeneralNameAr = "عام";

    public static readonly Guid GeneralExpensesId = new("8c2d3b6e-1f4a-4c55-9e1d-6a7b0f2e9c41");
    public const string GeneralExpensesName = "General expenses";
    public const string GeneralExpensesNameAr = "مصروفات عامة";

    private PurchaseCategory() { }

    public PurchaseCategory(
        Guid id, string name, string? nameAr = null, ExpenseLine expenseLine = ExpenseLine.Controllable, CategoryKind kind = CategoryKind.RawMaterials)
    {
        if (!Enum.IsDefined(kind))
            throw new DomainException("Say whether the category is for raw materials or general expenses.");
        Id = id;
        Kind = kind;
        CreatedAtUtc = DateTime.UtcNow;
        Update(name, nameAr, expenseLine);
    }

    public static PurchaseCategory General() => new(GeneralId, GeneralName, GeneralNameAr);

    public static PurchaseCategory GeneralExpenses() => new(GeneralExpensesId, GeneralExpensesName, GeneralExpensesNameAr, kind: CategoryKind.Expenses);

    // Where something of that kind goes when no category is chosen.
    public static Guid DefaultOf(CategoryKind kind) => kind == CategoryKind.Expenses ? GeneralExpensesId : GeneralId;

    public string Name { get; private set; } = string.Empty;

    // The name as compared: capitals and extra spaces ignored, so "dry  goods" is Dry goods.
    public string NormalizedName { get; private set; } = string.Empty;

    public string? NameAr { get; private set; }
    public ExpenseLine ExpenseLine { get; private set; } = ExpenseLine.Controllable;
    public CategoryKind Kind { get; private set; } = CategoryKind.RawMaterials;
    public DateTime CreatedAtUtc { get; private set; }

    // The General of its kind.
    public bool IsGeneral => Id == GeneralId || Id == GeneralExpensesId;

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

    // For the other kind: only while nothing of the other kind is filed under it (holdingOther: its
    // raw materials or expense items in use), so nothing is left in a category not its own. A
    // General stays the kind it is.
    public void ChangeKind(CategoryKind kind, int holdingOther)
    {
        if (!Enum.IsDefined(kind))
            throw new DomainException("Say whether the category is for raw materials or general expenses.");
        if (kind == Kind)
            return;
        if (IsGeneral)
            throw new DomainException($"{Name} is where anything not yet sorted goes: it stays {(Kind == CategoryKind.Expenses ? "general expenses" : "raw materials")}.");
        if (holdingOther > 0)
            throw new DomainException(
                Kind == CategoryKind.RawMaterials
                    ? $"{Name} holds {holdingOther} raw {(holdingOther == 1 ? "material" : "materials")}: move them to a raw materials category, or delete {Name} and convert them, first."
                    : $"{Name} holds {holdingOther} expense {(holdingOther == 1 ? "item" : "items")}: move them to a general expenses category, or delete {Name} and convert them, first.");
        Kind = kind;
    }

    public static string Normalize(string name) => Collapse(name).ToUpperInvariant();

    private static string Collapse(string name) => string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

// How many raw materials and expense items are filed under a category, archived ones too; and of
// them, the ones in use.
public sealed record PurchaseCategoryUse(int Materials, int ExpenseItems, int ActiveMaterials = 0, int ActiveExpenseItems = 0)
{
    // In use, and of the kind the category is not: filed there before the kinds were kept apart.
    public int Misplaced(CategoryKind kind) => kind == CategoryKind.RawMaterials ? ActiveExpenseItems : ActiveMaterials;
}

public interface IPurchaseCategoryRepository : IRepository<PurchaseCategory>
{
    Task<IReadOnlyList<PurchaseCategory>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<PurchaseCategory?> GetByNormalizedNameAsync(string normalizedName, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, PurchaseCategoryUse>> GetUseAsync(CancellationToken cancellationToken = default);

    // Only once nothing is filed under it, and never a General.
    void Remove(PurchaseCategory category);
}
