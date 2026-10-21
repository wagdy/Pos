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
//                                    any number of them: what it kept while the till was away.
// POST cdata?SN=…&table=USERINFO     its users, a line each (PIN=…⇥Name=…⇥…), when asked: their
//      (or OPERLOG, "USER PIN=…")    number and name are kept, nothing else (no password or card).
// POST cdata?SN=…&table=…            anything else (operations): taken as read, not kept.
//                                    Fingerprint and face templates are never kept.
// GET  getrequest?SN=…               every few seconds: a command a manager left for it
//                                    (C:<n>:DATA QUERY USERINFO, …ATTLOG StartTime=…), or OK.
// POST devicecmd?SN=…                its answer to a command: ID=<n>&Return=<code>&CMD=…
[ApiController]
[AllowAnonymous]
[Route("iclock")]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed partial class IclockController(ISender sender, ILogger<IclockController> logger) : ControllerBase
{
    // A day's punches are a few hundred lines; a device sending its whole log after weeks away, or
    // read again, tens of thousands, which it splits. A line is under 60 bytes.
    private const int MaxBody = 16 * 1024 * 1024;

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
        if (string.Equals(table, "USERINFO", StringComparison.OrdinalIgnoreCase) || string.Equals(table, "OPERLOG", StringComparison.OrdinalIgnoreCase))
        {
            var users = lines.Select(User).OfType<DeviceUserLine>().ToList();
            if (users.Count == 0)
            {
                var contact = await sender.Send(new DeviceContactCommand(serial!, Address), cancellationToken);
                return contact.Approved ? Text($"OK:{lines.Count}") : Refused();
            }

            var kept = await sender.Send(new RecordDeviceUsersCommand(serial!, users), cancellationToken);
            if (kept is null)
                return Refused();
            logger.LogInformation("Attendance device {Serial} listed {Users} users", serial, kept);
            return Text($"OK:{lines.Count}");
        }

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
        if (!contact.Approved)
            return Refused();
        var command = await sender.Send(new NextDeviceCommandCommand(serial!), cancellationToken);
        if (command is not null)
            logger.LogInformation("Attendance device {Serial} handed {Command}", serial, command);
        return Text(command ?? "OK");
    }

    [HttpPost("devicecmd")]
    [RequestSizeLimit(64 * 1024)]
    public async Task<IActionResult> CommandResult([FromQuery(Name = "SN")] string? serial, CancellationToken cancellationToken)
    {
        if (!IsSerial(serial))
            return BadRequest("SN");
        var contact = await sender.Send(new DeviceContactCommand(serial!, Address), cancellationToken);
        if (!contact.Approved)
            return Refused();
        var answers = (await LinesAsync(cancellationToken)).Select(Answer).OfType<(int, int)>().ToList();
        if (answers.Count > 0)
            await sender.Send(new DeviceCommandAnswersCommand(serial!, answers), cancellationToken);
        return Text("OK");
    }

    // "ID=5&Return=0&CMD=DATA": the command's number and its return code. Null for anything else.
    public static (int Number, int ReturnCode)? Answer(string line)
    {
        var fields = line.Split('&').Select(f => f.Split('=', 2)).Where(f => f.Length == 2).ToDictionary(f => f[0].Trim(), f => f[1].Trim(), StringComparer.OrdinalIgnoreCase);
        return fields.TryGetValue("ID", out var id) && int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            && fields.TryGetValue("Return", out var ret) && int.TryParse(ret, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code)
            ? (number, code)
            : null;
    }

    // "PIN=7⇥Name=Hassan⇥Pri=0⇥Passwd=…" (USERINFO) or "USER PIN=7⇥Name=Hassan…" (OPERLOG): the
    // user's number and name, and nothing else. Null for anything else, fingerprints and faces
    // ("FP PIN=…", "FACE PIN=…", "BIODATA …") above all.
    public static DeviceUserLine? User(string line)
    {
        var text = line.Trim();
        if (text.StartsWith("USER ", StringComparison.OrdinalIgnoreCase))
            text = text[5..].TrimStart();
        if (!text.StartsWith("PIN=", StringComparison.OrdinalIgnoreCase))
            return null;
        var fields = text.Split('\t').Select(f => f.Split('=', 2)).Where(f => f.Length == 2)
            .GroupBy(f => f[0].Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First()[1], StringComparer.OrdinalIgnoreCase);
        var pin = fields.GetValueOrDefault("PIN")?.Trim();
        if (pin is not { Length: > 0 and <= 24 } || !UserPattern().IsMatch(pin))
            return null;
        return new DeviceUserLine(pin, fields.GetValueOrDefault("Name")?.Trim());
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
