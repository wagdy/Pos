using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Application.Approvals;
using OtantikPos.Inventory.Application.Treasury;
using OtantikPos.Inventory.Domain.Treasury;

namespace OtantikPos.Application.Tests;

// The treasury's dates. After midnight, before the business day starts, the restaurant's calendar
// is a day ahead of the business day: an entry dated that calendar day is taken, never a day later.
public class TreasuryTests
{
    private readonly TestPos _pos = new();

    private Task<SafeVoucherResultDto> Enter(SafeTransactionKind kind, decimal amount, DateOnly date, DateOnly? takings = null, FundingSource? source = null) =>
        _pos.Send(new RecordSafeVoucherCommand(
            Guid.NewGuid(), new SafeVoucher(Guid.NewGuid(), kind, amount, date, source, takings), null, "staff-omar", "Omar", CanApprove: true));

    [Fact]
    public async Task After_midnight_the_calendar_day_is_taken_as_well_as_the_business_day()
    {
        _pos.Dates.BusinessDayBehind = 1;
        var calendar = DateOnly.FromDateTime(DateTime.UtcNow);
        var business = calendar.AddDays(-1);

        // The screens send the business day; the calendar day is taken too.
        var opening = await Enter(SafeTransactionKind.OpeningBalance, 1_000, business, source: FundingSource.CarriedOver);
        var transfer = await Enter(SafeTransactionKind.RevenueTransfer, 300, calendar, takings: calendar);
        Assert.Equal((business, calendar, calendar), (opening.Transaction!.BusinessDate, transfer.Transaction!.BusinessDate, transfer.Transaction.RevenueDate!.Value));

        // A day after the calendar is the future either way.
        var future = await Assert.ThrowsAsync<DomainException>(() =>
            Enter(SafeTransactionKind.Funding, 10, calendar.AddDays(1), source: FundingSource.Other));
        Assert.Equal("A safe entry is dated today or before: cash cannot move in the future.", future.Message);
        await Assert.ThrowsAsync<DomainException>(() =>
            Enter(SafeTransactionKind.RevenueTransfer, 10, calendar, takings: calendar.AddDays(1)));
    }

    private Task<SafeVoucherResultDto> Takings(decimal amount, bool canApprove, decimal? screenRate = null, SafeTransactionKind kind = SafeTransactionKind.RevenueTransfer)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return _pos.Send(new RecordSafeVoucherCommand(
            Guid.NewGuid(),
            new SafeVoucher(
                Guid.NewGuid(), kind, amount, today, RevenueDate: kind == SafeTransactionKind.RevenueTransfer ? today : null,
                ExpenseItemId: kind == SafeTransactionKind.ExpenseVoucher ? Guid.NewGuid() : null, IncludesTax: true, TaxPercent: screenRate),
            null, canApprove ? "staff-omar" : "staff-mona", canApprove ? "Omar" : "Mona", canApprove));
    }

    [Fact]
    public async Task Takings_with_the_tax_in_them_are_taken_off_at_the_tills_rate_never_the_screens()
    {
        var entered = await Takings(1_140, canApprove: true, screenRate: 1);

        var t = entered.Transaction!;
        Assert.Equal((1_140m, true, (decimal?)14m, 140m), (t.Amount, t.IncludesTax, t.TaxPercent, t.TaxAmount));
        // The whole sum counted is in the safe.
        Assert.Equal(1_140m, await _pos.Inventory.Safe.SafeBalanceAsync());

        // Only a day's takings have tax taken off.
        var expense = await Assert.ThrowsAsync<DomainException>(() => Takings(100, canApprove: true, kind: SafeTransactionKind.ExpenseVoucher));
        Assert.Equal("Only a day's takings are entered with the tax in them.", expense.Message);
    }

    [Fact]
    public async Task An_accountants_takings_show_the_tax_and_are_approved_at_the_rate_asked()
    {
        var asked = await Takings(570, canApprove: false);

        var request = asked.Request!;
        Assert.Contains(request.Changes, c => c.Label == "Tax in it (14%)" && c.After == "L.E 70.00");
        Assert.Contains(request.Changes, c => c.Label == "Takings before tax" && c.After == "L.E 500.00");

        // The till's rate changing in between does not change what was asked.
        _pos.Pricing.TaxPercentage = 10;
        await _pos.Send(new ApproveRequestCommand(request.Id, "Omar"));

        var approved = await _pos.Inventory.Safe.GetByIdAsync(request.RecordId);
        Assert.Equal((570m, (decimal?)14m, 70m, "Mona", "Omar"), (approved!.Amount, approved.TaxPercent, approved.TaxAmount, approved.RecordedBy, approved.ApprovedBy));
    }
}
