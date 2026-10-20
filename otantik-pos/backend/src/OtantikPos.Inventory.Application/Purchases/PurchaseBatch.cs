using System.Globalization;
using MediatR;
using Otantik.BuildingBlocks;
using OtantikPos.Hr.Domain;
using OtantikPos.Inventory.Application.Approvals;
using OtantikPos.Inventory.Application.Common;
using OtantikPos.Inventory.Application.Treasury;
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
// A supplier can be paid in it too (سداد مورد), an old debt settled with no invoice and no stock:
// from the safe or the day's takings, as the batch is paid, never on credit; against what is owed
// to them, never more, and taken off it on their account.
//
// An employee's advance (سلفة) can be paid in it as well, as a payment is: from the safe or the
// day's takings, never on credit, and owed by the employee until the payslip of its month deducts it.
//
// All or nothing: every invoice, payment and advance is checked before any is saved, and each
// problem is said by the one it is on. InvoiceId and a payment's or advance's TransactionId are
// made by the till: a batch sent again does not post an invoice, pay a supplier, or advance, twice.

public sealed record BatchInvoice(Guid InvoiceId, Guid? SupplierId, string? SupplierInvoiceNumber, IReadOnlyList<PurchaseInvoiceLineEntry> Lines);

public sealed record BatchPayment(Guid TransactionId, Guid SupplierId, decimal Amount, string? Notes = null);

public sealed record BatchAdvance(Guid TransactionId, Guid EmployeeId, decimal Amount, string? Notes = null);

// A payment the batch made (Applied), or asked of a manager (Requested): no cash moves until then.
public sealed record BatchPaymentDto(Guid TransactionId, Guid SupplierId, string Supplier, decimal Amount, string? Notes, ApprovalOutcome Outcome);

public sealed record BatchAdvanceDto(Guid TransactionId, Guid EmployeeId, string Employee, decimal Amount, string? Notes, ApprovalOutcome Outcome);

public sealed record PurchaseBatchResult(IReadOnlyList<PurchaseInvoiceDto> Invoices, IReadOnlyList<BatchPaymentDto> Payments, IReadOnlyList<BatchAdvanceDto> Advances);

public sealed record PostPurchaseBatchCommand(
    DateOnly InvoiceDate,
    PurchasePaymentMethod PaymentMethod,
    DateOnly? RevenueDate,
    IReadOnlyList<BatchInvoice> Invoices,
    string StaffName = "",
    string StaffId = "",
    bool CanApprove = true,
    IReadOnlyList<BatchPayment>? Payments = null,
    IReadOnlyList<BatchAdvance>? Advances = null) : IRequest<PurchaseBatchResult>, IRetryOnConflict;

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
    ISupplierLedgerRepository suppliersLedger,
    TreasuryWriter treasury,
    IEmployeeRepository employees,
    IUnitOfWork unitOfWork) : IRequestHandler<PostPurchaseBatchCommand, PurchaseBatchResult>
{
    public const int MaxInvoices = 50;

    public async Task<PurchaseBatchResult> Handle(PostPurchaseBatchCommand request, CancellationToken cancellationToken)
    {
        var payments = request.Payments ?? [];
        var advances = request.Advances ?? [];
        if (request.Invoices.Count == 0 && payments.Count == 0 && advances.Count == 0)
            throw new DomainException("Add an invoice, a supplier payment or an advance to the batch.");
        if (request.Invoices.Count + payments.Count + advances.Count > MaxInvoices)
            throw new DomainException($"A batch holds at most {MaxInvoices} invoices, payments and advances: send the rest as another.");
        if (request.PaymentMethod == PurchasePaymentMethod.BeforeTreasury)
            throw new DomainException("Before the treasury is for the invoices from before it, not for new ones.");
        if (request.Invoices.Select(i => i.InvoiceId).Distinct().Count() != request.Invoices.Count)
            throw new DomainException("The same invoice is in the batch twice.");
        if (payments.Count > 0 || advances.Count > 0)
        {
            if (request.PaymentMethod == PurchasePaymentMethod.Credit)
                throw new DomainException(payments.Count > 0
                    ? "A supplier payment is cash: pay the batch from the safe or a day's takings, or send the payments apart."
                    : "An advance is cash: pay the batch from the safe or a day's takings, or send the advances apart.");
            if (request.PaymentMethod == PurchasePaymentMethod.DayRevenue && request.RevenueDate is null)
                throw new DomainException("Choose the day whose takings pay them.");
            var ids = payments.Select(p => p.TransactionId).Concat(advances.Select(a => a.TransactionId)).Concat(request.Invoices.Select(i => i.InvoiceId)).ToList();
            if (ids.Distinct().Count() != ids.Count)
                throw new DomainException("The same payment is in the batch twice.");
        }

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

        var paid = new List<BatchPaymentDto>();
        // What each supplier is still owed as the payments above it are taken off.
        var owedLeft = new Dictionary<Guid, decimal>();
        for (var i = 0; i < payments.Count; i++)
        {
            var entry = payments[i];
            var name = (await suppliers.GetByIdAsync(entry.SupplierId, cancellationToken))?.Name;
            try
            {
                // Sent again after an answer was lost: paid, or asked, already.
                var paidAlready = await safe.GetByIdAsync(entry.TransactionId, cancellationToken) is not null;
                if (paidAlready || await requests.GetByIdAsync(entry.TransactionId, cancellationToken) is not null)
                {
                    var outcome = paidAlready ? ApprovalOutcome.Applied : ApprovalOutcome.Requested;
                    paid.Add(new BatchPaymentDto(entry.TransactionId, entry.SupplierId, name ?? "", entry.Amount, entry.Notes, outcome));
                    continue;
                }

                if (name is null)
                    throw new NotFoundException("Supplier", entry.SupplierId);
                if (entry.Amount <= 0)
                    throw new DomainException("Enter an amount more than nothing.");
                var after = owedLeft.ContainsKey(entry.SupplierId);
                var owed = after ? owedLeft[entry.SupplierId] : await suppliersLedger.BalanceAsync(entry.SupplierId, cancellationToken);
                if (entry.Amount > owed)
                    throw new DomainException(owed > 0
                        ? $"{name} is owed {Money(owed)}{(after ? " after the payments above" : "")}: pay that much or less."
                        : $"Nothing is owed to {name}{(after ? " after the payments above" : "")}.");
                owedLeft[entry.SupplierId] = owed - entry.Amount;

                var voucher = new SafeVoucher(
                    entry.TransactionId, SafeTransactionKind.SupplierPayment, entry.Amount, request.InvoiceDate,
                    RevenueDate: request.PaymentMethod == PurchasePaymentMethod.DayRevenue ? request.RevenueDate : null,
                    SupplierId: entry.SupplierId, Notes: entry.Notes);
                if (request.CanApprove)
                {
                    await treasury.ApplyAsync(voucher, request.StaffName, null, cancellationToken);
                    paid.Add(new BatchPaymentDto(entry.TransactionId, entry.SupplierId, name, entry.Amount, voucher.Notes?.Trim(), ApprovalOutcome.Applied));
                }
                else
                {
                    await treasury.CheckAsync(voucher, cancellationToken);
                    var (summary, changes) = await treasury.DescribeAsync(voucher, cancellationToken);
                    requests.Add(ApprovalRequest.AskToChange(
                        entry.TransactionId, ApprovalModule.Treasury, ApprovalAction.Create, entry.TransactionId, summary, changes,
                        ApprovalPayload.Write(voucher), null, request.StaffId, request.StaffName));
                    paid.Add(new BatchPaymentDto(entry.TransactionId, entry.SupplierId, name, entry.Amount, voucher.Notes?.Trim(), ApprovalOutcome.Requested));
                }
            }
            catch (Exception error) when (error is DomainException or NotFoundException)
            {
                problems.Add($"Payment {i + 1}{(name is null ? "" : $" · {name}")}: {error.Message}");
            }
        }

        var advanced = new List<BatchAdvanceDto>();
        for (var i = 0; i < advances.Count; i++)
        {
            var entry = advances[i];
            var name = (await employees.GetByIdAsync(entry.EmployeeId, cancellationToken))?.Name;
            try
            {
                // Sent again after an answer was lost: paid, or asked, already.
                var paidAlready = await safe.GetByIdAsync(entry.TransactionId, cancellationToken) is not null;
                if (paidAlready || await requests.GetByIdAsync(entry.TransactionId, cancellationToken) is not null)
                {
                    var outcome = paidAlready ? ApprovalOutcome.Applied : ApprovalOutcome.Requested;
                    advanced.Add(new BatchAdvanceDto(entry.TransactionId, entry.EmployeeId, name ?? "", entry.Amount, entry.Notes, outcome));
                    continue;
                }

                var voucher = new SafeVoucher(
                    entry.TransactionId, SafeTransactionKind.EmployeeAdvance, entry.Amount, request.InvoiceDate,
                    RevenueDate: request.PaymentMethod == PurchasePaymentMethod.DayRevenue ? request.RevenueDate : null,
                    Notes: entry.Notes, EmployeeId: entry.EmployeeId);
                if (request.CanApprove)
                {
                    await treasury.ApplyAsync(voucher, request.StaffName, null, cancellationToken);
                    advanced.Add(new BatchAdvanceDto(entry.TransactionId, entry.EmployeeId, name!, entry.Amount, voucher.Notes?.Trim(), ApprovalOutcome.Applied));
                }
                else
                {
                    await treasury.CheckAsync(voucher, cancellationToken);
                    var (summary, changes) = await treasury.DescribeAsync(voucher, cancellationToken);
                    requests.Add(ApprovalRequest.AskToChange(
                        entry.TransactionId, ApprovalModule.Treasury, ApprovalAction.Create, entry.TransactionId, summary, changes,
                        ApprovalPayload.Write(voucher), null, request.StaffId, request.StaffName));
                    advanced.Add(new BatchAdvanceDto(entry.TransactionId, entry.EmployeeId, name!, entry.Amount, voucher.Notes?.Trim(), ApprovalOutcome.Requested));
                }
            }
            catch (Exception error) when (error is DomainException or NotFoundException)
            {
                problems.Add($"Advance {i + 1}{(name is null ? "" : $" · {name}")}: {error.Message}");
            }
        }

        if (problems.Count > 0)
            throw new DomainException(string.Join("\n", problems));

        await unitOfWork.SaveChangesAsync(cancellationToken);
        var reading = new InvoiceReading(materials, expenses, categories, suppliers, safe, requests);
        var dtos = new List<PurchaseInvoiceDto>();
        foreach (var invoice in made)
            dtos.Add(await reading.DtoAsync(invoice, cancellationToken));
        return new PurchaseBatchResult(dtos, paid, advanced);
    }

    private static string Money(decimal value) => $"L.E {value.ToString("N2", CultureInfo.InvariantCulture)}";
}
