using MediatR;
using Otantik.BuildingBlocks;
using OtantikPos.Hr.Domain;
using OtantikPos.Hr.Domain.Attendance;
using OtantikPos.Hr.Domain.Payroll;
using OtantikPos.Inventory.Application.Common;

namespace OtantikPos.Inventory.Application.Hr;

// Public holidays (العطلات الرسمية): entered by a manager each year, as the government sets them.
// Each is everyone's paid day off, and a holiday allowance for whoever works it. A month whose pay
// is finalized for anyone keeps its holidays as they were, as it keeps its days.

public sealed record PublicHolidayDto(Guid Id, string Name, DateOnly FirstDay, DateOnly LastDay, int Days, string SetBy, DateTime SetAtUtc)
{
    public static PublicHolidayDto From(PublicHoliday h) => new(h.Id, h.Name, h.FirstDay, h.LastDay, h.Days, h.SetBy, h.SetAtUtc);
}

// A year's holidays, by their first day: any with a day in the year.
public sealed record GetPublicHolidaysQuery(int Year) : IRequest<IReadOnlyList<PublicHolidayDto>>;

internal sealed class GetPublicHolidaysHandler(IPublicHolidayRepository holidays) : IRequestHandler<GetPublicHolidaysQuery, IReadOnlyList<PublicHolidayDto>>
{
    public async Task<IReadOnlyList<PublicHolidayDto>> Handle(GetPublicHolidaysQuery request, CancellationToken cancellationToken)
    {
        if (request.Year is < 2000 or > 2100)
            throw new DomainException("Choose a year from 2000 to 2100.");
        var list = await holidays.GetBetweenAsync(new DateOnly(request.Year, 1, 1), new DateOnly(request.Year, 12, 31), cancellationToken);
        return list.Select(PublicHolidayDto.From).ToList();
    }
}

// A holiday added under this id, or changed. HolidayId is made by the till.
public sealed record SavePublicHolidayCommand(Guid HolidayId, string Name, DateOnly FirstDay, DateOnly LastDay, string StaffName)
    : IRequest<PublicHolidayDto>, IRetryOnConflict;

internal sealed class SavePublicHolidayHandler(IPublicHolidayRepository holidays, IPayrollSlipRepository slips, IUnitOfWork unitOfWork)
    : IRequestHandler<SavePublicHolidayCommand, PublicHolidayDto>
{
    public async Task<PublicHolidayDto> Handle(SavePublicHolidayCommand request, CancellationToken cancellationToken)
    {
        var holiday = await holidays.GetByIdAsync(request.HolidayId, cancellationToken);
        if (request.LastDay >= request.FirstDay)
        {
            var other = (await holidays.GetBetweenAsync(request.FirstDay, request.LastDay, cancellationToken)).FirstOrDefault(h => h.Id != request.HolidayId);
            if (other is not null)
                throw new DomainException($"{other.Name} is on {Holidays.Dates(other)} already: a day is one holiday.");
        }

        if (holiday is not null)
            await Holidays.EnsureOpenAsync(slips, holiday.FirstDay, holiday.LastDay, cancellationToken);
        await Holidays.EnsureOpenAsync(slips, request.FirstDay, request.LastDay < request.FirstDay ? request.FirstDay : request.LastDay, cancellationToken);

        if (holiday is null)
        {
            holiday = new PublicHoliday(request.HolidayId, request.Name, request.FirstDay, request.LastDay, request.StaffName);
            holidays.Add(holiday);
        }
        else
        {
            holiday.Change(request.Name, request.FirstDay, request.LastDay, request.StaffName);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return PublicHolidayDto.From(holiday);
    }
}

public sealed record DeletePublicHolidayCommand(Guid HolidayId) : IRequest;

internal sealed class DeletePublicHolidayHandler(IPublicHolidayRepository holidays, IPayrollSlipRepository slips, IUnitOfWork unitOfWork)
    : IRequestHandler<DeletePublicHolidayCommand>
{
    public async Task Handle(DeletePublicHolidayCommand request, CancellationToken cancellationToken)
    {
        var holiday = await holidays.GetRequiredAsync(request.HolidayId, cancellationToken);
        await Holidays.EnsureOpenAsync(slips, holiday.FirstDay, holiday.LastDay, cancellationToken);
        holidays.Remove(holiday);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

internal static class Holidays
{
    // "6 Oct", or "30 Mar–1 Apr".
    public static string Dates(PublicHoliday h) => h.Days == 1 ? $"{h.FirstDay:d MMM}" : $"{h.FirstDay:d MMM}–{h.LastDay:d MMM}";

    // A finalized slip keeps its month's days as they were: no holiday is added to it, moved in or
    // out of it, or taken off it after.
    public static async Task EnsureOpenAsync(IPayrollSlipRepository slips, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken)
    {
        for (var month = PayrollMonth.Of(firstDay); month <= lastDay; month = month.AddMonths(1))
        {
            if ((await slips.GetForMonthAsync(month, cancellationToken)).Any(s => s.IsFinalized))
                throw new DomainException($"Pay for {month:MMMM yyyy} is finalized: its holidays are not changed after it.");
        }
    }
}
