using Otantik.BuildingBlocks;

namespace OtantikPos.Inventory.Domain.Expenses;

// Something bought that is not stock: cleaning supplies, a repair, the electricity bill. It goes
// on purchase invoices beside the raw materials, but is never counted, never in a recipe and
// never in the stock ledger: posting an invoice only records what it cost, under its category,
// for the profit and loss. Kept apart from the raw materials so it cannot reach those screens.
//
// One on an invoice is never removed, only archived, as a supplier is. Its id is made by the till.
//
// Marked an asset (AssetCategoryId: plates, a fryer), each posted invoice line of it is also an
// asset in Custody & Assets, in the store, of that category: its quantity at the line's cost a
// piece. Not stock either way.
public sealed class ExpenseItem : AggregateRoot
{
    private ExpenseItem() { }

    public ExpenseItem(Guid id, string name, Guid categoryId, string? nameAr = null, string? unit = null)
    {
        Id = id;
        CreatedAtUtc = DateTime.UtcNow;
        Update(name, categoryId, nameAr, unit);
    }

    public string Name { get; private set; } = string.Empty;

    // The name as compared: capitals and extra spaces ignored.
    public string NormalizedName { get; private set; } = string.Empty;

    public string? NameAr { get; private set; }
    public Guid CategoryId { get; private set; }

    // What a line of it is usually counted in (bottle, visit, month), for the invoice to start
    // from. None: a line is just its amount.
    public string? Unit { get; private set; }

    // The custody category its purchases are assets of; none for a plain expense.
    public Guid? AssetCategoryId { get; private set; }
    public bool IsAsset => AssetCategoryId is not null;

    public bool IsArchived { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? ArchivedAtUtc { get; private set; }

    public void Update(string name, Guid categoryId, string? nameAr, string? unit)
    {
        var clean = Collapse(name ?? string.Empty);
        if (clean.Length == 0)
            throw new DomainException("Give the expense a name.");
        if (clean.Length > 200 || (nameAr is not null && Collapse(nameAr).Length > 200))
            throw new DomainException("Keep the expense's name under 200 characters.");
        if (unit?.Trim().Length > 32)
            throw new DomainException("Keep the unit under 32 characters.");

        Name = clean;
        NormalizedName = Normalize(clean);
        NameAr = string.IsNullOrWhiteSpace(nameAr) ? null : Collapse(nameAr);
        Unit = string.IsNullOrWhiteSpace(unit) ? null : unit.Trim();
        MoveTo(categoryId);
    }

    public void MoveTo(Guid categoryId)
    {
        if (categoryId == Guid.Empty)
            throw new DomainException("Choose the expense's category.");
        CategoryId = categoryId;
    }

    // From the invoices posted after it: those posted before stay as they were.
    public void MarkAsAsset(Guid? assetCategoryId) =>
        AssetCategoryId = assetCategoryId == Guid.Empty ? throw new DomainException("Choose the asset category, or none.") : assetCategoryId;

    // Off the list new invoices pick from; on the invoices it is already on.
    public void Archive()
    {
        if (IsArchived)
            return;
        IsArchived = true;
        ArchivedAtUtc = DateTime.UtcNow;
    }

    public void Restore()
    {
        IsArchived = false;
        ArchivedAtUtc = null;
    }

    public static string Normalize(string name) => Collapse(name).ToUpperInvariant();

    private static string Collapse(string name) => string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

public interface IExpenseItemRepository : IRepository<ExpenseItem>
{
    Task<IReadOnlyList<ExpenseItem>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ExpenseItem>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ExpenseItem>> GetInCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default);

    Task<ExpenseItem?> GetByNormalizedNameAsync(string normalizedName, CancellationToken cancellationToken = default);

    // Only one that is on no invoice.
    void Remove(ExpenseItem item);
}
