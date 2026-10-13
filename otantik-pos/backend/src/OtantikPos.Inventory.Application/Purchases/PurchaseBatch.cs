using MediatR;
using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Application.Common;
using OtantikPos.Inventory.Domain.Approvals;
using OtantikPos.Inventory.Domain.Categories;
using OtantikPos.Inventory.Domain.Expenses;
using OtantikPos.Inventory.Domain.Purchases;
using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Inventory.Domain.Suppliers;
using OtantikPos.Inventory.Domain.Treasury;

namespace OtantikPos.Inventory.Application.Purchases;

// A day's purchases entered together (يومية المشتريات المجمعة): the date and how they were paid,
// chosen once, and an invoice for each supplier. Each is its own purchase invoice, as if entered
// alone: recorded, posted, its stock in, and paid as chosen. On credit it is owed to its supplier;
// in cash it is paid for a manager, and asked of one, a request each, for anyone else. Never one
// invoice for the lot, so each supplier's account and the safe stay exact.
//
// All or nothing: every invoice is checked before any is saved, and each problem is said by the
// invoice it is on. InvoiceId is made by the till: a batch sent again does not post an invoice twice.

public sealed record BatchInvoice(Guid InvoiceId, Guid? SupplierId, string? SupplierInvoiceNumber, IReadOnlyList<PurchaseInvoiceLineEntry> Lines);

public sealed record PostPurchaseBatchCommand(
    DateOnly InvoiceDate,
    PurchasePaymentMethod PaymentMethod,
    DateOnly? RevenueDate,
    IReadOnlyList<BatchInvoice> Invoices,
    string StaffName = "",
    string StaffId = "",
    bool CanApprove = true) : IRequest<IReadOnlyList<PurchaseInvoiceDto>>, IRetryOnConflict;

internal sealed class PostPurchaseBatchHandler(
    IPurchaseInvoiceRepository invoices,
    InvoiceRecorder recorder,
    InvoicePoster poster,
    IRawMaterialRepository materials,
    IExpenseItemRepository expenses,
    IPurchaseCategoryRepository categories,
    ISupplierRepository suppliers,
    ISafeTransactionRepository safe,
    IApprovalRequestRepository requests,
    IUnitOfWork unitOfWork) : IRequestHandler<PostPurchaseBatchCommand, IReadOnlyList<PurchaseInvoiceDto>>
{
    public const int MaxInvoices = 50;

    public async Task<IReadOnlyList<PurchaseInvoiceDto>> Handle(PostPurchaseBatchCommand request, CancellationToken cancellationToken)
    {
        if (request.Invoices.Count == 0)
            throw new DomainException("Add an invoice to the batch.");
        if (request.Invoices.Count > MaxInvoices)
            throw new DomainException($"A batch holds at most {MaxInvoices} invoices: send the rest as another.");
        if (request.PaymentMethod == PurchasePaymentMethod.BeforeTreasury)
            throw new DomainException("Before the treasury is for the invoices from before it, not for new ones.");
        if (request.Invoices.Select(i => i.InvoiceId).Distinct().Count() != request.Invoices.Count)
            throw new DomainException("The same invoice is in the batch twice.");

        var made = new List<PurchaseInvoice>();
        var problems = new List<string>();
        for (var i = 0; i < request.Invoices.Count; i++)
        {
            var entry = request.Invoices[i];
            try
            {
                // Sent again after an answer was lost: posted already, it is not posted twice.
                if (await invoices.GetByIdAsync(entry.InvoiceId, cancellationToken) is { Status: PurchaseInvoiceStatus.Posted } posted)
                {
                    made.Add(posted);
                    continue;
                }

                if (entry.Lines.Count == 0)
                    throw new DomainException("It has no lines: add what was bought, or take the invoice off the batch.");
                var invoice = await recorder.RecordAsync(
                    new SavePurchaseInvoiceCommand(
                        entry.InvoiceId, entry.SupplierId, entry.SupplierInvoiceNumber, request.InvoiceDate, entry.Lines, request.StaffName,
                        request.PaymentMethod, request.RevenueDate),
                    cancellationToken);
                await poster.PostAsync(invoice, request.StaffName, request.StaffId, request.CanApprove, cancellationToken);
                made.Add(invoice);
            }
            catch (Exception error) when (error is DomainException or NotFoundException)
            {
                var supplier = entry.SupplierId is { } id ? (await suppliers.GetByIdAsync(id, cancellationToken))?.Name : null;
                problems.Add($"Invoice {i + 1}{(supplier is null ? "" : $" · {supplier}")}: {error.Message}");
            }
        }

        if (problems.Count > 0)
            throw new DomainException(string.Join("\n", problems));

        await unitOfWork.SaveChangesAsync(cancellationToken);
        var reading = new InvoiceReading(materials, expenses, categories, suppliers, safe, requests);
        var dtos = new List<PurchaseInvoiceDto>();
        foreach (var invoice in made)
            dtos.Add(await reading.DtoAsync(invoice, cancellationToken));
        return dtos;
    }
}
