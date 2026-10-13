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
        var future = await Assert.ThrowsAsync<Otantik.BuildingBlocks.DomainException>(() =>
            Enter(SafeTransactionKind.Funding, 10, calendar.AddDays(1), source: FundingSource.Other));
        Assert.Equal("A safe entry is dated today or before: cash cannot move in the future.", future.Message);
        await Assert.ThrowsAsync<Otantik.BuildingBlocks.DomainException>(() =>
            Enter(SafeTransactionKind.RevenueTransfer, 10, calendar, takings: calendar.AddDays(1)));
    }
}
