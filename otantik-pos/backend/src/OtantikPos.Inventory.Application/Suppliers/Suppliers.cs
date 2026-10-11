using MediatR;
using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Application.Common;
using OtantikPos.Inventory.Domain.Purchases;
using OtantikPos.Inventory.Domain.Suppliers;

namespace OtantikPos.Inventory.Application.Suppliers;

// The suppliers the invoices are filed under. SupplierId is made by the till.

public sealed record SupplierDto(
    Guid Id,
    string Name,
    string? ContactPerson,
    string? Phone,
    string? Email,
    string? Notes,
    bool IsArchived,
    // Invoices it is on, drafts included: any at all and deleting it archives it instead.
    int Invoices,
    decimal PostedTotal,
    DateTime? LastPostedAtUtc)
{
    public static SupplierDto From(Supplier supplier, SupplierUse? use) => new(
        supplier.Id, supplier.Name, supplier.ContactPerson, supplier.Phone, supplier.Email, supplier.Notes, supplier.IsArchived,
        use?.Invoices ?? 0, use?.PostedTotal ?? 0, use?.LastPostedAtUtc);
}

public enum SupplierRemoval
{
    Deleted = 1,

    // It is on invoices: kept for them, off the list for new ones.
    Archived = 2,
}

public sealed record SupplierRemovalDto(SupplierRemoval Outcome);

// By name. The archived ones only when asked for: the Suppliers screen shows them to restore.
public sealed record GetSuppliersQuery(bool IncludeArchived = false) : IRequest<IReadOnlyList<SupplierDto>>;

internal sealed class GetSuppliersQueryHandler(ISupplierRepository suppliers, IPurchaseInvoiceRepository invoices)
    : IRequestHandler<GetSuppliersQuery, IReadOnlyList<SupplierDto>>
{
    public async Task<IReadOnlyList<SupplierDto>> Handle(GetSuppliersQuery request, CancellationToken cancellationToken)
    {
        var use = await invoices.GetSupplierUseAsync(cancellationToken);
        return (await suppliers.GetAllAsync(cancellationToken))
            .Where(s => request.IncludeArchived || !s.IsArchived)
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(s => SupplierDto.From(s, use.GetValueOrDefault(s.Id)))
            .ToList();
    }
}

// Adds the supplier, or changes its details. Two suppliers cannot share a name, archived or not:
// the invoices would not say which was which.
public sealed record SaveSupplierCommand(
    Guid SupplierId, string Name, string? ContactPerson = null, string? Phone = null, string? Email = null, string? Notes = null)
    : IRequest<SupplierDto>;

internal sealed class SaveSupplierCommandHandler(ISupplierRepository suppliers, IPurchaseInvoiceRepository invoices, IUnitOfWork unitOfWork)
    : IRequestHandler<SaveSupplierCommand, SupplierDto>
{
    public async Task<SupplierDto> Handle(SaveSupplierCommand request, CancellationToken cancellationToken)
    {
        var details = new SupplierDetails(request.Name ?? string.Empty, request.ContactPerson, request.Phone, request.Email, request.Notes);
        if (!string.IsNullOrWhiteSpace(details.Name)
            && await suppliers.GetByNormalizedNameAsync(Supplier.Normalize(details.Name), cancellationToken) is { } namesake
            && namesake.Id != request.SupplierId)
        {
            throw new DomainException(namesake.IsArchived
                ? $"{namesake.Name} is already a supplier, archived. Restore it from the archived suppliers."
                : $"{namesake.Name} is already a supplier.");
        }

        var supplier = await suppliers.GetByIdAsync(request.SupplierId, cancellationToken);
        if (supplier is null)
            suppliers.Add(supplier = new Supplier(request.SupplierId, details));
        else
            supplier.Update(details);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return SupplierDto.From(supplier, (await invoices.GetSupplierUseAsync(cancellationToken)).GetValueOrDefault(supplier.Id));
    }
}

// Deleted if it is on no invoice; archived if it is, so the invoices keep it.
public sealed record DeleteSupplierCommand(Guid SupplierId) : IRequest<SupplierRemovalDto>;

internal sealed class DeleteSupplierCommandHandler(ISupplierRepository suppliers, IPurchaseInvoiceRepository invoices, IUnitOfWork unitOfWork)
    : IRequestHandler<DeleteSupplierCommand, SupplierRemovalDto>
{
    public async Task<SupplierRemovalDto> Handle(DeleteSupplierCommand request, CancellationToken cancellationToken)
    {
        // Already gone: a retry of a delete that worked.
        if (await suppliers.GetByIdAsync(request.SupplierId, cancellationToken) is not { } supplier)
            return new SupplierRemovalDto(SupplierRemoval.Deleted);

        var outcome = SupplierRemoval.Deleted;
        if (await invoices.AnyForSupplierAsync(supplier.Id, cancellationToken))
        {
            supplier.Archive();
            outcome = SupplierRemoval.Archived;
        }
        else
        {
            suppliers.Remove(supplier);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new SupplierRemovalDto(outcome);
    }
}

// Back on the list new invoices pick from.
public sealed record RestoreSupplierCommand(Guid SupplierId) : IRequest<SupplierDto>;

internal sealed class RestoreSupplierCommandHandler(ISupplierRepository suppliers, IPurchaseInvoiceRepository invoices, IUnitOfWork unitOfWork)
    : IRequestHandler<RestoreSupplierCommand, SupplierDto>
{
    public async Task<SupplierDto> Handle(RestoreSupplierCommand request, CancellationToken cancellationToken)
    {
        var supplier = await suppliers.GetRequiredAsync(request.SupplierId, cancellationToken);
        supplier.Restore();
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return SupplierDto.From(supplier, (await invoices.GetSupplierUseAsync(cancellationToken)).GetValueOrDefault(supplier.Id));
    }
}
