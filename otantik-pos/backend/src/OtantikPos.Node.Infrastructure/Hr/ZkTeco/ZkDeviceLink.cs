using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Application.Hr;

namespace OtantikPos.Node.Infrastructure.Hr.ZkTeco;

// HR's IAttendanceDeviceLink over ZKTeco's own protocol: a test is a connection and what the
// device says it is; a read is its user table and its attendance log. Each its own connection,
// closed after.
internal sealed class ZkDeviceLink(ILogger<ZkDeviceLink> logger) : IAttendanceDeviceLink
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(20);

    public async Task<DeviceProbe> ProbeAsync(string address, int port, int commKey, CancellationToken cancellationToken)
    {
        try
        {
            await using var zk = await ZkClient.ConnectAsync(address, port, commKey, ProbeTimeout, cancellationToken);
            var serial = await zk.OptionAsync("~SerialNumber", cancellationToken);
            var model = await zk.OptionAsync("~DeviceName", cancellationToken);
            var firmware = await zk.FirmwareAsync(cancellationToken);
            var (users, records) = await zk.SizesAsync(cancellationToken);
            return new DeviceProbe(
                true, $"Connected to {model ?? "the device"}{(serial is null ? "" : $", serial {serial}")}: {users} users and {records} punches on it.",
                serial, model, firmware, users, records);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation(ex, "Attendance device at {Address}:{Port} did not answer", address, port);
            return new DeviceProbe(false, Explain(ex, address, port));
        }
    }

    public async Task<DeviceReading> ReadAsync(string address, int port, int commKey, CancellationToken cancellationToken)
    {
        try
        {
            await using var zk = await ZkClient.ConnectAsync(address, port, commKey, ReadTimeout, cancellationToken);
            var serial = await zk.OptionAsync("~SerialNumber", cancellationToken);
            if (string.IsNullOrWhiteSpace(serial))
                throw new DomainException("The device did not give its serial number, which its punches are kept by.");
            var model = await zk.OptionAsync("~DeviceName", cancellationToken);
            var (userCount, recordCount) = await zk.SizesAsync(cancellationToken);
            var users = await zk.UsersAsync(userCount, cancellationToken);
            var punches = await zk.AttendanceAsync(recordCount, users, cancellationToken);
            logger.LogInformation("Read attendance device {Serial} at {Address}: {Users} users, {Punches} punches", serial, address, users.Count, punches.Count);
            return new DeviceReading(
                serial.Trim(), model,
                users.Where(u => IsNumber(u.Number)).Select(u => new DeviceUserLine(u.Number, u.Name)).ToList(),
                punches.Where(p => IsNumber(p.Number)).Select(p => new DevicePunch(p.Number, p.PunchedAt, p.State, p.Verify)).ToList());
        }
        catch (DomainException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Attendance device at {Address}:{Port} could not be read", address, port);
            throw new DomainException(Explain(ex, address, port));
        }
    }

    // A user number as the punches carry it: letters and digits.
    private static bool IsNumber(string number) => number.Length is > 0 and <= 24 && number.All(char.IsAsciiLetterOrDigit);

    private static string Explain(Exception ex, string address, int port) => ex switch
    {
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } =>
            $"{address} answered, but nothing listens on port {port}. Its own port may be closed while it uses ADMS (Cloud Server): its punches still come that way.",
        SocketException or OperationCanceledException or TimeoutException =>
            $"No answer from {address} on port {port}. Is the device on, and is this machine on the restaurant's network?",
        ZkException zk => zk.Message,
        IOException => $"{address} closed the connection on port {port}: is that the device's port?",
        _ => $"The device at {address} could not be read: {ex.Message}",
    };
}
