using Otantik.BuildingBlocks;

namespace OtantikPos.Inventory.Domain.Suppliers;

// Who the restaurant buys from: the name the invoices are filed under, and how to reach them.
// A supplier on an invoice is never removed, only archived: it leaves the list new invoices pick
// from, and the invoices it is on keep showing it. One never invoiced can go entirely.
//
// Its id is made by the till, so a retried save adds the supplier once.
public sealed class Supplier : AggregateRoot
{
    private Supplier() { }

    public Supplier(Guid id, SupplierDetails details)
    {
        Id = id;
        CreatedAtUtc = DateTime.UtcNow;
        Update(details);
    }

    public string Name { get; private set; } = string.Empty;

    // The name as compared: capitals and extra spaces ignored, so "Nile  farms" is Nile Farms.
    public string NormalizedName { get; private set; } = string.Empty;

    public string? ContactPerson { get; private set; }
    public string? Phone { get; private set; }
    public string? Email { get; private set; }
    public string? Notes { get; private set; }

    public bool IsArchived { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? ArchivedAtUtc { get; private set; }

    public void Update(SupplierDetails details)
    {
        var name = Clean(details.Name) ?? throw new DomainException("Give the supplier a name.");
        if (name.Length > 200)
            throw new DomainException("Keep the supplier's name under 200 characters.");
        if (Clean(details.ContactPerson)?.Length > 200 || Clean(details.Email)?.Length > 200)
            throw new DomainException("Keep the contact's name and email under 200 characters.");
        if (Clean(details.Phone)?.Length > 32)
            throw new DomainException("Keep the phone number under 32 characters.");
        if (Clean(details.Notes)?.Length > 500)
            throw new DomainException("Keep the notes under 500 characters.");
        if (Clean(details.Email) is { } email && !email.Contains('@'))
            throw new DomainException("That email address is missing its @.");

        Name = Collapse(name);
        NormalizedName = Normalize(name);
        ContactPerson = Clean(details.ContactPerson);
        Phone = Clean(details.Phone);
        Email = Clean(details.Email);
        Notes = Clean(details.Notes);
    }

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

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record SupplierDetails(string Name, string? ContactPerson = null, string? Phone = null, string? Email = null, string? Notes = null);

public interface ISupplierRepository : IRepository<Supplier>
{
    Task<IReadOnlyList<Supplier>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<Supplier?> GetByNormalizedNameAsync(string normalizedName, CancellationToken cancellationToken = default);

    // Only one that is on no invoice.
    void Remove(Supplier supplier);
}
