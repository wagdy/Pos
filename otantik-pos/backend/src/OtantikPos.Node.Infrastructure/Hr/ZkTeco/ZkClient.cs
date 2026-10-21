using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace OtantikPos.Node.Infrastructure.Hr.ZkTeco;

// ZKTeco's own protocol over TCP (port 4370 by default), as the standalone devices speak it: the
// till connects to the device and reads it, where ADMS has the device call the till. ZKTeco does
// not publish it; this follows the open-source pyzk client, packet for packet.
//
// Every packet is a frame: 50 50 82 7D, then the length of what follows (little-endian), then a
// command header (command, checksum, session, reply number: four little-endian ushorts) and its
// data. Large answers (the user table, the attendance log) come through a buffer: the device is
// asked to prepare one, then read in chunks of up to 0xFFC0 bytes, each a "prepare data" frame,
// data frames and an OK.
public sealed class ZkClient : IAsyncDisposable
{
    public const int DefaultPort = 4370;

    private const ushort CmdConnect = 1000;
    private const ushort CmdExit = 1001;
    private const ushort CmdAuth = 1102;
    private const ushort CmdGetVersion = 1100;
    private const ushort CmdOptions = 11;
    private const ushort CmdFreeSizes = 50;
    private const ushort CmdUsers = 9;
    private const ushort CmdAttendance = 13;
    private const ushort CmdPrepareBuffer = 1503;
    private const ushort CmdReadBuffer = 1504;
    private const ushort CmdFreeData = 1502;
    private const ushort CmdPrepareData = 1500;
    private const ushort CmdData = 1501;
    private const ushort AckOk = 2000;
    private const ushort AckUnauthorized = 2005;
    private const byte UsersFunction = 5;
    private const int MaxChunk = 0xFFC0;
    private static readonly byte[] Magic = [0x50, 0x50, 0x82, 0x7D];

    private readonly TcpClient _tcp;
    private readonly TimeSpan _timeout;
    private NetworkStream _stream = null!;
    private ushort _session;
    private ushort _reply = ushort.MaxValue - 1;

    private ZkClient(TcpClient tcp, TimeSpan timeout)
    {
        _tcp = tcp;
        _timeout = timeout;
    }

    // Connected and, with a comm key set on the device, authenticated. `timeout` bounds each read.
    public static async Task<ZkClient> ConnectAsync(string host, int port, int commKey, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var tcp = new TcpClient();
        var client = new ZkClient(tcp, timeout);
        try
        {
            using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connecting.CancelAfter(timeout);
                await tcp.ConnectAsync(host, port, connecting.Token);
            }

            client._stream = tcp.GetStream();
            var answer = await client.SendAsync(CmdConnect, [], cancellationToken);
            client._session = answer.Session;
            if (answer.Command == AckUnauthorized)
                answer = await client.SendAsync(CmdAuth, CommKey(commKey, client._session), cancellationToken);
            if (answer.Command == AckUnauthorized)
                throw new ZkException("The device refused the comm key: give the one set on it (COMM. → Comm Key).");
            if (answer.Command != AckOk)
                throw new ZkException($"The device answered the connection with {answer.Command}.");
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    // "~SerialNumber" → "CQZ7232060112"; null when the device has no such option.
    public async Task<string?> OptionAsync(string name, CancellationToken cancellationToken)
    {
        var answer = await SendAsync(CmdOptions, Encoding.ASCII.GetBytes(name + "\0"), cancellationToken);
        if (answer.Command != AckOk)
            return null;
        var text = Encoding.ASCII.GetString(answer.Data).Split('\0')[0];
        var equals = text.IndexOf('=');
        return equals < 0 ? null : text[(equals + 1)..].Trim();
    }

    public async Task<string?> FirmwareAsync(CancellationToken cancellationToken)
    {
        var answer = await SendAsync(CmdGetVersion, [], cancellationToken);
        return answer.Command == AckOk ? Encoding.ASCII.GetString(answer.Data).Split('\0')[0].Trim() : null;
    }

    // How many users and attendance records it holds.
    public async Task<(int Users, int Records)> SizesAsync(CancellationToken cancellationToken)
    {
        var answer = await SendAsync(CmdFreeSizes, [], cancellationToken);
        if (answer.Command != AckOk || answer.Data.Length < 80)
            throw new ZkException("The device did not say how much it holds.");
        int Field(int i) => BinaryPrimitives.ReadInt32LittleEndian(answer.Data.AsSpan(i * 4));
        return (Field(4), Field(8));
    }

    // Its users: their number (the user id the punches carry) and name, nothing else.
    public async Task<IReadOnlyList<ZkUser>> UsersAsync(int users, CancellationToken cancellationToken)
    {
        if (users <= 0)
            return [];
        var data = await ReadBufferAsync(CmdUsers, UsersFunction, cancellationToken);
        return ParseUsers(data, users);
    }

    // Its attendance log, as it holds it.
    public async Task<IReadOnlyList<ZkPunch>> AttendanceAsync(int records, IReadOnlyList<ZkUser> users, CancellationToken cancellationToken)
    {
        if (records <= 0)
            return [];
        var data = await ReadBufferAsync(CmdAttendance, 0, cancellationToken);
        return ParseAttendance(data, records, users);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_tcp.Connected && _stream is not null)
                await SendAsync(CmdExit, [], CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ZkException or ObjectDisposedException)
        {
            // Leaving: the device drops it either way.
        }

        _tcp.Dispose();
    }

    // The table, after its four-byte total size: 72 bytes a user on most devices, 28 on older
    // ones; which is told by the size over the count.
    public static IReadOnlyList<ZkUser> ParseUsers(byte[] data, int users)
    {
        if (data.Length <= 4)
            return [];
        var total = (int)BinaryPrimitives.ReadUInt32LittleEndian(data);
        var size = users > 0 ? total / users : 72;
        var list = new List<ZkUser>();
        for (var at = 4; at + size <= data.Length && at - 4 < total; at += size)
        {
            var row = data.AsSpan(at, size);
            if (size == 28)
            {
                var name = Text(row.Slice(8, 8));
                var number = BinaryPrimitives.ReadUInt32LittleEndian(row[24..]).ToString(System.Globalization.CultureInfo.InvariantCulture);
                list.Add(new ZkUser(BinaryPrimitives.ReadUInt16LittleEndian(row), number, name));
            }
            else
            {
                list.Add(new ZkUser(BinaryPrimitives.ReadUInt16LittleEndian(row), Text(row.Slice(48, 24)), Text(row.Slice(11, 24))));
            }
        }

        return list;
    }

    // The log, after its four-byte total size: 40 bytes a record on most devices (the user id as
    // text), 16 (as a number) or 8 (the user's index) on others.
    public static IReadOnlyList<ZkPunch> ParseAttendance(byte[] data, int records, IReadOnlyList<ZkUser> users)
    {
        if (data.Length <= 4)
            return [];
        var total = (int)BinaryPrimitives.ReadUInt32LittleEndian(data);
        var size = records > 0 ? total / records : 40;
        var byIndex = users.GroupBy(u => u.Index).ToDictionary(g => g.Key, g => g.First().Number);
        var list = new List<ZkPunch>();
        for (var at = 4; at + size <= data.Length && at - 4 < total; at += size)
        {
            var row = data.AsSpan(at, size);
            switch (size)
            {
                case 8:
                {
                    var index = BinaryPrimitives.ReadUInt16LittleEndian(row);
                    list.Add(new ZkPunch(byIndex.GetValueOrDefault(index) ?? index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Time(row.Slice(3, 4)), row[7], row[2]));
                    break;
                }
                case 16:
                    list.Add(new ZkPunch(BinaryPrimitives.ReadUInt32LittleEndian(row).ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Time(row.Slice(4, 4)), row[9], row[8]));
                    break;
                default:
                    list.Add(new ZkPunch(Text(row.Slice(2, 24)), Time(row.Slice(27, 4)), row[31], row[26]));
                    break;
            }
        }

        return list;
    }

    // Seconds packed into one number: second, minute, hour, day (of 31), month (of 12), years
    // since 2000.
    public static DateTime Time(ReadOnlySpan<byte> bytes)
    {
        var t = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var second = (int)(t % 60); t /= 60;
        var minute = (int)(t % 60); t /= 60;
        var hour = (int)(t % 24); t /= 24;
        var day = (int)(t % 31) + 1; t /= 31;
        var month = (int)(t % 12) + 1; t /= 12;
        return new DateTime((int)t + 2000, month, day, hour, minute, second, DateTimeKind.Unspecified);
    }

    // The packet as pyzk makes it: the checksum over the header with the reply number before it
    // is counted on, then the header with the next one.
    public static byte[] Packet(ushort command, ReadOnlySpan<byte> data, ushort session, ushort reply, out ushort nextReply)
    {
        var body = new byte[8 + data.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(body, command);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), session);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6), reply);
        data.CopyTo(body.AsSpan(8));
        var checksum = Checksum(body);
        nextReply = (ushort)(reply + 1 >= ushort.MaxValue ? reply + 1 - ushort.MaxValue : reply + 1);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), checksum);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6), nextReply);
        var frame = new byte[8 + body.Length];
        Magic.CopyTo(frame, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), (uint)body.Length);
        body.CopyTo(frame, 8);
        return frame;
    }

    public static ushort Checksum(ReadOnlySpan<byte> bytes)
    {
        long sum = 0;
        var i = 0;
        for (; i + 1 < bytes.Length; i += 2)
        {
            sum += bytes[i] | (bytes[i + 1] << 8);
            if (sum > ushort.MaxValue)
                sum -= ushort.MaxValue;
        }

        if (i < bytes.Length)
            sum += bytes[i];
        while (sum > ushort.MaxValue)
            sum -= ushort.MaxValue;
        sum = ~sum;
        while (sum < 0)
            sum += ushort.MaxValue;
        return (ushort)sum;
    }

    // The comm key as the device checks it, made with the session it gave.
    public static byte[] CommKey(int key, ushort session, byte ticks = 50)
    {
        uint k = 0;
        for (var i = 0; i < 32; i++)
            k = (key & (1 << i)) != 0 ? (k << 1) | 1 : k << 1;
        k += session;
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, k);
        b[0] ^= (byte)'Z';
        b[1] ^= (byte)'K';
        b[2] ^= (byte)'S';
        b[3] ^= (byte)'O';
        // The two halves swapped.
        byte[] swapped = [b[2], b[3], b[0], b[1]];
        return [(byte)(swapped[0] ^ ticks), (byte)(swapped[1] ^ ticks), ticks, (byte)(swapped[3] ^ ticks)];
    }

    // A large answer through the device's buffer: prepared, read chunk by chunk, freed.
    private async Task<byte[]> ReadBufferAsync(ushort command, int function, CancellationToken cancellationToken)
    {
        var ask = new byte[11];
        ask[0] = 1;
        BinaryPrimitives.WriteInt16LittleEndian(ask.AsSpan(1), (short)command);
        BinaryPrimitives.WriteInt32LittleEndian(ask.AsSpan(3), function);
        var answer = await SendAsync(CmdPrepareBuffer, ask, cancellationToken);
        if (answer.Command == CmdData)
            return answer.Data;
        if (answer.Command != AckOk || answer.Data.Length < 5)
            throw new ZkException($"The device would not prepare its data ({answer.Command}).");

        var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(answer.Data.AsSpan(1));
        using var all = new MemoryStream(size);
        for (var start = 0; start < size; start += MaxChunk)
            all.Write(await ReadChunkAsync(start, Math.Min(MaxChunk, size - start), cancellationToken));
        await SendAsync(CmdFreeData, [], cancellationToken);
        return all.ToArray();
    }

    private async Task<byte[]> ReadChunkAsync(int start, int size, CancellationToken cancellationToken)
    {
        var ask = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(ask, start);
        BinaryPrimitives.WriteInt32LittleEndian(ask.AsSpan(4), size);
        var answer = await SendAsync(CmdReadBuffer, ask, cancellationToken);
        if (answer.Command == CmdData)
            return answer.Data;
        if (answer.Command != CmdPrepareData || answer.Data.Length < 4)
            throw new ZkException($"The device did not send its data ({answer.Command}).");

        var expected = (int)BinaryPrimitives.ReadUInt32LittleEndian(answer.Data);
        using var chunk = new MemoryStream(expected);
        while (chunk.Length < expected)
        {
            var part = await ReadFrameAsync(cancellationToken);
            if (part.Command != CmdData)
                throw new ZkException($"The device broke off its data ({part.Command}).");
            chunk.Write(part.Data);
        }

        var done = await ReadFrameAsync(cancellationToken);
        if (done.Command != AckOk)
            throw new ZkException($"The device did not finish its data ({done.Command}).");
        return chunk.ToArray();
    }

    private async Task<Frame> SendAsync(ushort command, byte[] data, CancellationToken cancellationToken)
    {
        var packet = Packet(command, data, _session, _reply, out _reply);
        await _stream.WriteAsync(packet, cancellationToken);
        var answer = await ReadFrameAsync(cancellationToken);
        _reply = answer.Reply;
        return answer;
    }

    // Each answer within the timeout: a device that stops answering is let go, not waited on.
    private async Task<Frame> ReadFrameAsync(CancellationToken cancellationToken)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        waiting.CancelAfter(_timeout);
        try
        {
            return await ReadFrameWithinAsync(waiting.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ZkException($"The device stopped answering for {_timeout.TotalSeconds:0} seconds.");
        }
    }

    private async Task<Frame> ReadFrameWithinAsync(CancellationToken cancellationToken)
    {
        var top = new byte[8];
        await _stream.ReadExactlyAsync(top, cancellationToken);
        if (!top.AsSpan(0, 4).SequenceEqual(Magic))
            throw new ZkException("The device's answer is not ZKTeco's: is this its port?");
        var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(top.AsSpan(4));
        if (length < 8 || length > 16 * 1024 * 1024)
            throw new ZkException("The device's answer is not ZKTeco's: is this its port?");
        var body = new byte[length];
        await _stream.ReadExactlyAsync(body, cancellationToken);
        return new Frame(
            BinaryPrimitives.ReadUInt16LittleEndian(body), BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4)),
            BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(6)), body[8..]);
    }

    private static string Text(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? bytes : bytes[..end]).Trim();
    }

    private sealed record Frame(ushort Command, ushort Session, ushort Reply, byte[] Data);
}

// A user on the device: its own index, the number the punches carry, the name typed on it.
public sealed record ZkUser(int Index, string Number, string Name);

// A punch as the device holds it: whose number, when (its clock), its state key and how it was
// recognised.
public sealed record ZkPunch(string Number, DateTime PunchedAt, int State, int Verify);

public sealed class ZkException(string message) : Exception(message);
