using Microsoft.Extensions.Options;
using OtantikPos.Node.Infrastructure.Common;

namespace OtantikPos.Node.Infrastructure.Tests;

// No database here: the business day is arithmetic on the restaurant's time zone.
public class RestaurantClockTests
{
    private static readonly RestaurantClock Cairo = new(Options.Create(new RestaurantOptions { TimeZoneId = "Africa/Cairo", BusinessDayStartsAtHour = 4 }));

    private static DateTime Utc(int year, int month, int day, int hour, int minute = 0) => new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    [Fact]
    public void A_business_day_runs_from_four_in_the_morning_to_four_the_next_restaurant_time()
    {
        // October: Egyptian summer time, UTC+3.
        Assert.Equal((Utc(2026, 10, 3, 1), Utc(2026, 10, 4, 1)), Cairo.Bounds(new DateOnly(2026, 10, 3)));
        // January: UTC+2.
        Assert.Equal((Utc(2026, 1, 15, 2), Utc(2026, 1, 16, 2)), Cairo.Bounds(new DateOnly(2026, 1, 15)));
    }

    [Fact]
    public void An_order_after_midnight_belongs_to_the_evening_before()
    {
        // 01:30 on the 4th in Cairo.
        Assert.Equal(new DateOnly(2026, 10, 3), Cairo.BusinessDateOf(Utc(2026, 10, 3, 22, 30)));
        // 04:30 on the 4th: a new day.
        Assert.Equal(new DateOnly(2026, 10, 4), Cairo.BusinessDateOf(Utc(2026, 10, 4, 1, 30)));
    }
}
