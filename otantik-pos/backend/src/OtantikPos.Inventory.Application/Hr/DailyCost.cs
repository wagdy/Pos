using MediatR;
using OtantikPos.Hr.Domain;
using OtantikPos.Hr.Domain.Attendance;
using OtantikPos.Hr.Domain.Employees;

namespace OtantikPos.Inventory.Application.Hr;

// A day's labour cost (تكلفة يوم): a quick reference, not the payroll. Everyone who punched in on
// the day, at their day's rate (a monthly salary over 30, or their daily wage), by department.
// That day's overtime pay is said beside it and not in Cost; the payslips deduct lateness and
// absence and add allowances at the month's end. Today's is the day so far.

public sealed record EmployeeDayCostDto(
    Guid EmployeeId,
    string Name,
    string JobTitle,
    Guid DepartmentId,
    EmploymentType EmploymentType,
    DayStatus Status,
    DateTime? FirstIn,
    DateTime? LastOut,
    int WorkedMinutes,
    int OvertimeMinutes,
    decimal DayRate,
    decimal OvertimePay,
    bool ForgotToCheckOut);

public sealed record DepartmentDayCostDto(Guid DepartmentId, string Name, int Worked, decimal Cost, decimal OvertimePay);

// Employed: everyone employed on the day; Worked: those of them who punched in.
public sealed record DailyLabourCostDto(
    DateOnly Date,
    int Employed,
    int Worked,
    decimal Cost,
    decimal OvertimePay,
    IReadOnlyList<DepartmentDayCostDto> Departments,
    IReadOnlyList<EmployeeDayCostDto> Employees);

public sealed record GetDailyLabourCostQuery(DateOnly Date) : IRequest<DailyLabourCostDto>;

internal sealed class GetDailyLabourCostHandler(IEmployeeRepository employees, IDepartmentRepository departments, Timesheets timesheets)
    : IRequestHandler<GetDailyLabourCostQuery, DailyLabourCostDto>
{
    public async Task<DailyLabourCostDto> Handle(GetDailyLabourCostQuery request, CancellationToken cancellationToken)
    {
        var people = (await employees.GetAllAsync(cancellationToken)).Where(e => e.IsEmployedOn(request.Date)).ToList();
        var (_, sheets) = await timesheets.ReadAsync(people, request.Date, request.Date, cancellationToken);
        var worked = sheets
            .Where(s => s.Days[0].Evaluation.Punches > 0)
            .Select(s =>
            {
                var (e, day) = (s.Employee, s.Days[0]);
                var d = day.Evaluation;
                return new EmployeeDayCostDto(
                    e.Id, e.Name, e.JobTitle, e.DepartmentId, e.EmploymentType, d.Status, d.FirstIn, d.LastOut, d.WorkedMinutes, d.OvertimeMinutes,
                    Money(e.DailyRate), Money(d.OvertimeMinutes * e.MinuteRate), day.ForgotToCheckOut);
            })
            .OrderBy(c => c.Name)
            .ToList();
        var names = (await departments.GetAllAsync(cancellationToken)).ToDictionary(d => d.Id, d => d.Name);
        var byDepartment = worked
            .GroupBy(c => c.DepartmentId)
            .Select(g => new DepartmentDayCostDto(g.Key, names.GetValueOrDefault(g.Key) ?? "—", g.Count(), g.Sum(c => c.DayRate), g.Sum(c => c.OvertimePay)))
            .OrderByDescending(d => d.Cost)
            .ThenBy(d => d.Name)
            .ToList();
        return new DailyLabourCostDto(
            request.Date, people.Count, worked.Count, worked.Sum(c => c.DayRate), worked.Sum(c => c.OvertimePay), byDepartment, worked);
    }

    private static decimal Money(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);
}
