using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OtantikPos.Inventory.Application.Hr;

namespace OtantikPos.Node.Api.Controllers;

// The attendance devices' own door: ZKTeco's ADMS push protocol ("iclock"), as a ZKTeco K40 Pro
// speaks it over the restaurant's network. Its paths are the device's, fixed, so it is outside
// /api.
//
// A device cannot sign in: it names itself by its serial number (?SN=), over plain HTTP. So these
// endpoints are anonymous, and the serial number is the check. A device first heard from is kept
// as waiting and refused (403) until a manager approves it on the HR devices tab. It keeps its own
// log and, once approved, sends everything after its stamp, so nothing is lost meanwhile.
//
// GET  cdata?SN=…&options=all       it starts: its options, with the stamp to send after.
// POST cdata?SN=…&table=ATTLOG       punches, a line each: PIN⇥yyyy-MM-dd HH:mm:ss⇥status⇥verify⇥…
// POST cdata?SN=…&table=…            anything else (operations, users): taken as read, not kept.
//                                    Fingerprint templates are never kept.
// GET  getrequest?SN=…               every few seconds: OK, as no command is ever queued yet.
// POST devicecmd?SN=…                a command's result: OK.
[ApiController]
[AllowAnonymous]
[Route("iclock")]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed partial class IclockController(ISender sender, ILogger<IclockController> logger) : ControllerBase
{
    // A day's punches are a few hundred lines; a device sending its whole log after weeks away,
    // tens of thousands, which it splits.
    private const int MaxBody = 2 * 1024 * 1024;

    [HttpGet("cdata")]
    public async Task<IActionResult> Start([FromQuery(Name = "SN")] string? serial, CancellationToken cancellationToken)
    {
        if (!IsSerial(serial))
            return BadRequest("SN");
        var contact = await sender.Send(new DeviceContactCommand(serial!, Address), cancellationToken);
        if (!contact.Approved)
            return Refused();
        return Text(string.Join(
            "\r\n",
            $"GET OPTION FROM: {serial}",
            $"ATTLOGStamp={contact.AttendanceStamp ?? "None"}",
            "OPERLOGStamp=9999",
            "ATTPHOTOStamp=None",
            "ErrorDelay=30",
            "Delay=10",
            "TransTimes=00:00;14:05",
            "TransInterval=1",
            "TransFlag=TransData AttLog\tOpLog",
            "Realtime=1",
            "Encrypt=None"));
    }

    [HttpPost("cdata")]
    [RequestSizeLimit(MaxBody)]
    public async Task<IActionResult> Upload(
        [FromQuery(Name = "SN")] string? serial, [FromQuery] string? table, [FromQuery(Name = "Stamp")] string? stamp, CancellationToken cancellationToken)
    {
        if (!IsSerial(serial))
            return BadRequest("SN");
        var lines = await LinesAsync(cancellationToken);
        if (!string.Equals(table, "ATTLOG", StringComparison.OrdinalIgnoreCase))
        {
            var contact = await sender.Send(new DeviceContactCommand(serial!, Address), cancellationToken);
            return contact.Approved ? Text($"OK:{lines.Count}") : Refused();
        }

        var punches = new List<DevicePunch>();
        foreach (var line in lines)
        {
            if (Attendance(line) is { } punch)
                punches.Add(punch);
            else
                logger.LogWarning("Attendance device {Serial} sent a line that is not a punch: {Line}", serial, line.Length > 120 ? line[..120] : line);
        }

        var result = await sender.Send(
            new RecordDevicePunchesCommand(serial!, punches, stamp is { Length: <= 32 } && StampPattern().IsMatch(stamp) ? stamp : null, Address),
            cancellationToken);
        if (result is null)
            return Refused();
        if (result.Kept > 0)
            logger.LogInformation("Attendance device {Serial}: {Kept} new punches of {Received}", serial, result.Kept, result.Received);
        return Text($"OK:{lines.Count}");
    }

    [HttpGet("getrequest")]
    public async Task<IActionResult> Poll([FromQuery(Name = "SN")] string? serial, CancellationToken cancellationToken)
    {
        if (!IsSerial(serial))
            return BadRequest("SN");
        var contact = await sender.Send(new DeviceContactCommand(serial!, Address), cancellationToken);
        return contact.Approved ? Text("OK") : Refused();
    }

    [HttpPost("devicecmd")]
    [RequestSizeLimit(64 * 1024)]
    public async Task<IActionResult> CommandResult([FromQuery(Name = "SN")] string? serial, CancellationToken cancellationToken)
    {
        if (!IsSerial(serial))
            return BadRequest("SN");
        var contact = await sender.Send(new DeviceContactCommand(serial!, Address), cancellationToken);
        return contact.Approved ? Text("OK") : Refused();
    }

    // "7⇥2026-10-12 10:04:31⇥0⇥1⇥0⇥0⇥0": the user number, the time on the device's clock, the
    // state key pressed, how they were recognised. Null for anything else.
    public static DevicePunch? Attendance(string line)
    {
        var fields = line.Split('\t');
        if (fields.Length < 2)
            return null;
        var user = fields[0].Trim();
        if (user.Length is 0 or > 32 || !UserPattern().IsMatch(user))
            return null;
        if (!DateTime.TryParseExact(fields[1].Trim(), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
            return null;
        int? Code(int i) => fields.Length > i && int.TryParse(fields[i].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var code) ? code : null;
        return new DevicePunch(user, at, Code(2), Code(3));
    }

    private async Task<IReadOnlyList<string>> LinesAsync(CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(cancellationToken);
        return body.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
    }

    private string? Address => HttpContext.Connection.RemoteIpAddress?.ToString();

    private static bool IsSerial(string? serial) => serial is { Length: > 0 and <= 64 } && SerialPattern().IsMatch(serial);

    private ContentResult Text(string body) => Content(body, "text/plain", Encoding.UTF8);

    private ObjectResult Refused() => StatusCode(StatusCodes.Status403Forbidden, "This device waits for a manager to approve it on the till.");

    [GeneratedRegex("^[A-Za-z0-9-]+$")]
    private static partial Regex SerialPattern();

    [GeneratedRegex("^[A-Za-z0-9]+$")]
    private static partial Regex UserPattern();

    [GeneratedRegex("^[0-9]+$")]
    private static partial Regex StampPattern();
}
