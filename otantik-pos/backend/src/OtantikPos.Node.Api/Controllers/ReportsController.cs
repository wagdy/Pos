using MediatR;
using Microsoft.AspNetCore.Mvc;
using OtantikPos.Ordering.Application.Reports;

namespace OtantikPos.Node.Api.Controllers;

// The till's own reports, from its own database: there with or without the internet. Who may
// see them is decided in the use case (anyone who may see every order: cashiers and managers).
[ApiController]
[Route("api/reports")]
public sealed class ReportsController(ISender sender) : ControllerBase
{
    // One business day (Restaurant:BusinessDayStartsAtHour local time to the same hour the next
    // day); today's when no date is given. ?date=2026-10-03
    [HttpGet("day")]
    public Task<DayReport> Day([FromQuery] DateOnly? date, CancellationToken cancellationToken) =>
        sender.Send(new GetDayReportQuery(date), cancellationToken);
}
