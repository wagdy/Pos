using Microsoft.EntityFrameworkCore;
using Otantik.BuildingBlocks;
using OtantikPos.Node.Infrastructure.Common;
using OtantikPos.Node.Infrastructure.Persistence;
using static OtantikPos.Node.Infrastructure.Costing.CostingService;

namespace OtantikPos.Node.Infrastructure.Costing;

// The worksheet's Income Statement column: the chosen months at a year's pace, as the template
// lays it out. Gross sales = weekly sales × 52; cost of sales at the months' own ratio; fixed
// costs are the monthly costs entered, averaged over the months that have them, × 12.
public sealed record BreakEvenWorksheet(
    DateOnly From,
    DateOnly To,
    // Days of sales the pace is taken from: the whole of each month, up to today in this one.
    int Days,
    decimal WeeklySales,
    decimal GrossSales,
    decimal CostOfSales,
    // Cost of sales ÷ sales, unrounded: the options are worked out at the same ratio.
    decimal CostOfSalesRatio,
    decimal GrossProfit,
    decimal ControllableCosts,
    decimal OccupationCost,
    decimal Interest,
    decimal Depreciation,
    decimal TotalFixedCosts,
    decimal RestaurantProfit,
    // Total fixed costs ÷ profit margin. Null when every sale loses money: no level of sales breaks even.
    decimal? BreakEvenYearlySales,
    decimal? BreakEvenWeeklySales,
    // Months in the range with no costs entered; with none at all, there are no fixed costs to break even on.
    IReadOnlyList<string> MonthsWithoutCosts,
    IReadOnlyList<decimal> Scenarios);

// The sixth template: the Break-Even Analysis and Scenario Worksheet.
public sealed class BreakEvenService(NodeDbContext db, RestaurantClock clock, KpiService kpis)
{
    private const int WeeksInYear = 52;

    public async Task<BreakEvenWorksheet> GetAsync(int fromYear, int fromMonth, int toYear, int toMonth, CancellationToken cancellationToken)
    {
        if (fromMonth is < 1 or > 12 || toMonth is < 1 or > 12)
            throw new DomainException("Choose the months.");
        var first = new DateOnly(fromYear, fromMonth, 1);
        var lastMonth = new DateOnly(toYear, toMonth, 1);
        var today = clock.BusinessDateOf(DateTime.UtcNow);
        if (lastMonth < first)
            throw new DomainException("The first month must come before the last.");
        if (lastMonth > new DateOnly(today.Year, today.Month, 1))
            throw new DomainException("Choose months that have begun.");
        if (lastMonth.AddMonths(-23) > first)
            throw new DomainException("Choose two years at most.");

        decimal revenue = 0, costOfSales = 0, controllable = 0, occupation = 0, interest = 0, depreciation = 0;
        var days = 0;
        var withCosts = 0;
        var withoutCosts = new List<string>();
        for (var month = first; month <= lastMonth; month = month.AddMonths(1))
        {
            var report = await kpis.GetAsync(month.Year, month.Month, cancellationToken);
            revenue += report.Statement.Revenue;
            costOfSales += report.Statement.CostOfSales;
            var monthEnd = month.AddMonths(1).AddDays(-1);
            days += (monthEnd < today ? monthEnd : today).DayNumber - month.DayNumber + 1;

            var e = report.Expenses;
            if (e.WagesAndBenefits is null && e.OtherControllableCosts is null && e.OccupationCost is null && e.Interest is null && e.Depreciation is null)
            {
                withoutCosts.Add(month.ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture));
                continue;
            }
            withCosts++;
            controllable += (e.WagesAndBenefits ?? 0) + (e.OtherControllableCosts ?? 0);
            occupation += e.OccupationCost ?? 0;
            interest += e.Interest ?? 0;
            depreciation += e.Depreciation ?? 0;
        }

        decimal Yearly(decimal total) => withCosts > 0 ? Money(total * 12 / withCosts) : 0;
        var weekly = days > 0 ? revenue / days * 7 : 0;
        var grossSales = weekly * WeeksInYear;
        var ratio = revenue > 0 ? costOfSales / revenue : 0;
        var yearlyCostOfSales = grossSales * ratio;
        var grossProfit = grossSales - yearlyCostOfSales;
        var (yearlyControllable, yearlyOccupation, yearlyInterest, yearlyDepreciation) =
            (Yearly(controllable), Yearly(occupation), Yearly(interest), Yearly(depreciation));
        var fixedCosts = yearlyControllable + yearlyOccupation + yearlyInterest + yearlyDepreciation;

        // Each L.E of sales leaves (1 − ratio) towards the fixed costs.
        decimal? breakEven = withCosts > 0 && ratio < 1 ? fixedCosts / (1 - ratio) : null;

        var settings = await db.CostingSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken) ?? new CostingSettings();
        var periodEnd = lastMonth.AddMonths(1).AddDays(-1);
        return new BreakEvenWorksheet(
            first, periodEnd < today ? periodEnd : today, days,
            Money(weekly), Money(grossSales), Money(yearlyCostOfSales), ratio, Money(grossProfit),
            yearlyControllable, yearlyOccupation, yearlyInterest, yearlyDepreciation, fixedCosts,
            Money(grossProfit - fixedCosts),
            Money(breakEven), breakEven is { } b ? Money(b / WeeksInYear) : null,
            withoutCosts, settings.BreakEvenScenarios);
    }

    // The four options' weekly sales, kept for next time.
    public async Task<IReadOnlyList<decimal>> SaveScenariosAsync(IReadOnlyList<decimal> scenarios, CancellationToken cancellationToken)
    {
        if (scenarios.Count > 4 || scenarios.Any(s => s < 0))
            throw new DomainException("Up to four options, each a weekly sales figure of zero or more.");
        var row = await db.CostingSettings.SingleOrDefaultAsync(cancellationToken);
        if (row is null)
            db.CostingSettings.Add(row = new CostingSettings());
        row.BreakEvenScenarios = scenarios.Select(s => Money(s)).ToList();
        await db.SaveChangesAsync(cancellationToken);
        return row.BreakEvenScenarios;
    }
}
