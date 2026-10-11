using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Domain.Suppliers;

namespace Otantik.Domain.Tests;

// Who the restaurant buys from. Named as the manager writes it, compared without capitals or
// extra spaces; archived rather than removed once it is on an invoice.
public class SupplierTests
{
    [Fact]
    public void A_supplier_keeps_its_name_tidied_and_compares_it_loosely()
    {
        var supplier = new Supplier(Guid.NewGuid(), new SupplierDetails("  Nile   Farms ", " Hassan ", " 0100 123 4567 ", " orders@nile.example ", " Delivers Mondays "));

        Assert.Equal(("Nile Farms", "NILE FARMS"), (supplier.Name, supplier.NormalizedName));
        Assert.Equal(("Hassan", "0100 123 4567", "orders@nile.example", "Delivers Mondays"), (supplier.ContactPerson, supplier.Phone, supplier.Email, supplier.Notes));
        Assert.Equal(supplier.NormalizedName, Supplier.Normalize("nile farms"));

        supplier.Update(new SupplierDetails("Nile Farms Co.", Phone: "  "));
        Assert.Equal(("Nile Farms Co.", (string?)null, (string?)null), (supplier.Name, supplier.ContactPerson, supplier.Phone));
    }

    [Fact]
    public void A_supplier_refuses_what_cannot_be_its_details()
    {
        Assert.Throws<DomainException>(() => new Supplier(Guid.NewGuid(), new SupplierDetails("  ")));
        Assert.Throws<DomainException>(() => new Supplier(Guid.NewGuid(), new SupplierDetails(new string('x', 201))));
        Assert.Throws<DomainException>(() => new Supplier(Guid.NewGuid(), new SupplierDetails("Nile Farms", Email: "orders.nile.example")));
        Assert.Throws<DomainException>(() => new Supplier(Guid.NewGuid(), new SupplierDetails("Nile Farms", Phone: new string('1', 33))));
    }

    [Fact]
    public void Archived_and_restored()
    {
        var supplier = new Supplier(Guid.NewGuid(), new SupplierDetails("Nile Farms"));

        supplier.Archive();
        var archivedAt = supplier.ArchivedAtUtc;
        supplier.Archive();
        Assert.Equal((true, archivedAt), (supplier.IsArchived, supplier.ArchivedAtUtc));

        supplier.Restore();
        Assert.Equal((false, (DateTime?)null), (supplier.IsArchived, supplier.ArchivedAtUtc));
    }
}
