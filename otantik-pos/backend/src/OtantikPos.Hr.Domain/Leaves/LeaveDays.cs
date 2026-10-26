using OtantikPos.Hr.Domain.Attendance;
using OtantikPos.Hr.Domain.Employees;

namespace OtantikPos.Hr.Domain.Leaves;

// The days a leave takes: those of its dates the employee would work, neither a weekly day off of
// theirs nor a public holiday, and while employed. A Sunday to Thursday asked by one off on
// Fridays and Saturdays is five days; with a holiday on the Tuesday, four.
public static class LeaveDays
{
    public static IReadOnlyList<DateOnly> Of(Employee employee, DateOnly startDate, DateOnly endDate, IEnumerable<PublicHoliday> holidays)
    {
        var list = holidays.ToList();
        var days = new List<DateOnly>();
        for (var day = startDate; day <= endDate; day = day.AddDays(1))
        {
            if (employee.IsEmployedOn(day) && !employee.IsDayOff(day) && !list.Any(h => h.Covers(day)))
                days.Add(day);
        }

        return days;
    }
}
