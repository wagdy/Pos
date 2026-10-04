using Microsoft.Extensions.Options;
using OtantikPos.Ordering.Application.Ports;

namespace OtantikPos.Node.Infrastructure.Common;

public sealed class RestaurantOptions
{
    public const string Section = "Restaurant";

    // IANA id. Works on Linux, macOS and Windows alike on .NET 6 and later.
    public string TimeZoneId { get; set; } = "Africa/Cairo";

    // The hour the business day rolls over, in local time. With 4, an order taken at 01:30
    // belongs to the previous evening's service: it continues that night's ticket numbers
    // rather than starting again at 1.
    public int BusinessDayStartsAtHour { get; set; } = 4;
}

// Everything is stored in UTC. This is the one place that knows the restaurant's local time,
// for the two things that need it: the time printed on a ticket, and which business day an
// order belongs to (and so which day's report it is in).
public sealed class RestaurantClock(IOptions<RestaurantOptions> options) : IBusinessCalendar
{
    private readonly TimeZoneInfo _zone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZoneId);
    private readonly int _dayStartsAtHour = options.Value.BusinessDayStartsAtHour;

    public DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, _zone);

    public DateOnly BusinessDateOf(DateTime utc) => DateOnly.FromDateTime(ToLocal(utc).AddHours(-_dayStartsAtHour));

    public (DateTime FromUtc, DateTime ToUtc) Bounds(DateOnly businessDate)
    {
        var start = businessDate.ToDateTime(new TimeOnly(_dayStartsAtHour, 0));
        return (ToUtc(start), ToUtc(start.AddDays(1)));
    }

    // A local time skipped by a daylight-saving change does not exist; the hour after it does.
    private DateTime ToUtc(DateTime local)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(_zone.IsInvalidTime(local) ? local.AddHours(1) : local, _zone);
    }
}
