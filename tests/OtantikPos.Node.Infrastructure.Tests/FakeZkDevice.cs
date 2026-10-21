using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OtantikPos.Node.Infrastructure.Tests;

// A ZKTeco attendance device on its own port, as far as the till can tell: the protocol's frames,
// a session, a comm key when one is set, and its user table and attendance log handed over
// through its buffer in data frames of `FrameSize` bytes. `Modern` is the K40's records (72 bytes a
// user, 40 a punch); otherwise the older 28 and 16.
public sealed class FakeZkDevice : IDisposable
{
    public const ushort Session = 0x2A17;

    private const ushort Connect = 1000, Exit = 1001, Auth = 1102, Version = 1100, Options = 11, FreeSizes = 50;
    private const ushort PrepareBuffer = 1503, ReadBuffer = 1504, FreeData = 1502, PrepareData = 1500, Data = 1501;
    private const ushort Ok = 2000, Error = 2001, Unauthorized = 2005;
    private static readonly byte[] Magic = [0x50, 0x50, 0x82, 0x7D];

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

    public FakeZkDevice()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptAsync();
    }

    public int Port { get; }
    public string SerialNumber { get; init; } = "CQZ7232060112";
    public string Model { get; init; } = "K40 Pro";
    public int CommKey { get; init; }
    public bool Modern { get; init; } = true;
    public int FrameSize { get; init; } = 1024;

    // Never answers: a device hung, or something else on the port that takes connections.
    public bool Silent { get; init; }

    public List<(int Index, string Number, string Name)> Users { get; } = [];
    public List<(string Number, DateTime At, int State, int Verify)> Punches { get; } = [];

    // Every command it was sent, in order.
    public ConcurrentQueue<ushort> Commands { get; } = new();

    public static uint Encode(DateTime t) =>
        (uint)((((((t.Year - 2000) * 12 + (t.Month - 1)) * 31 + (t.Day - 1)) * 24 + t.Hour) * 60 + t.Minute) * 60 + t.Second);

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync();
                _ = Task.Run(() => ServeAsync(client));
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var authenticated = CommKey == 0;
            byte[]? prepared = null;
            try
            {
                while (true)
                {
                    var top = new byte[8];
                    await stream.ReadExactlyAsync(top);
                    if (!top.AsSpan(0, 4).SequenceEqual(Magic))
                        return;
                    var body = new byte[BinaryPrimitives.ReadUInt32LittleEndian(top.AsSpan(4))];
                    await stream.ReadExactlyAsync(body);
                    var command = BinaryPrimitives.ReadUInt16LittleEndian(body);
                    var reply = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(6));
                    var data = body.AsSpan(8).ToArray();
                    Commands.Enqueue(command);
                    if (Silent)
                        continue;

                    Task Answer(ushort code, byte[]? payload = null) => stream.WriteAsync(Frame(code, reply, payload ?? [])).AsTask();

                    if (command == Connect)
                    {
                        await Answer(authenticated ? Ok : Unauthorized);
                        continue;
                    }

                    if (command == Auth)
                    {
                        authenticated = data.AsSpan().SequenceEqual(OtantikPos.Node.Infrastructure.Hr.ZkTeco.ZkClient.CommKey(CommKey, Session));
                        await Answer(authenticated ? Ok : Unauthorized);
                        continue;
                    }

                    if (!authenticated)
                    {
                        await Answer(Unauthorized);
                        continue;
                    }

                    switch (command)
                    {
                        case Exit:
                            await Answer(Ok);
                            return;
                        case Options:
                            var name = Encoding.ASCII.GetString(data).TrimEnd('\0');
                            var value = name switch { "~SerialNumber" => SerialNumber, "~DeviceName" => Model, _ => null };
                            await (value is null ? Answer(Error) : Answer(Ok, Encoding.ASCII.GetBytes($"{name}={value}\0")));
                            break;
                        case Version:
                            await Answer(Ok, Encoding.ASCII.GetBytes("Ver 6.60 Apr 28 2017\0"));
                            break;
                        case FreeSizes:
                            var sizes = new byte[92];
                            BinaryPrimitives.WriteInt32LittleEndian(sizes.AsSpan(4 * 4), Users.Count);
                            BinaryPrimitives.WriteInt32LittleEndian(sizes.AsSpan(8 * 4), Punches.Count);
                            await Answer(Ok, sizes);
                            break;
                        case PrepareBuffer:
                            prepared = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(1)) switch
                            {
                                9 when BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(3)) == 5 => UserTable(),
                                13 => AttendanceLog(),
                                _ => null,
                            };
                            if (prepared is null)
                            {
                                await Answer(Error);
                                break;
                            }

                            var ready = new byte[9];
                            BinaryPrimitives.WriteUInt32LittleEndian(ready.AsSpan(1), (uint)prepared.Length);
                            await Answer(Ok, ready);
                            break;
                        case ReadBuffer:
                            var start = BinaryPrimitives.ReadInt32LittleEndian(data);
                            var size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
                            var chunk = prepared!.AsSpan(start, size).ToArray();
                            var coming = new byte[8];
                            BinaryPrimitives.WriteUInt32LittleEndian(coming, (uint)chunk.Length);
                            await Answer(PrepareData, coming);
                            for (var at = 0; at < chunk.Length; at += FrameSize)
                                await Answer(Data, chunk[at..Math.Min(chunk.Length, at + FrameSize)]);
                            await Answer(Ok);
                            break;
                        case FreeData:
                            prepared = null;
                            await Answer(Ok);
                            break;
                        default:
                            await Answer(Error);
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or EndOfStreamException or ObjectDisposedException)
            {
                // The till hung up.
            }
        }
    }

    private static byte[] Frame(ushort command, ushort reply, byte[] data)
    {
        var frame = new byte[16 + data.Length];
        Magic.CopyTo(frame, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), (uint)(8 + data.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(8), command);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(12), Session);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(14), reply);
        data.CopyTo(frame, 16);
        return frame;
    }

    private byte[] UserTable()
    {
        var size = Modern ? 72 : 28;
        var table = new byte[4 + size * Users.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(table, (uint)(size * Users.Count));
        for (var i = 0; i < Users.Count; i++)
        {
            var (index, number, name) = Users[i];
            var row = table.AsSpan(4 + i * size, size);
            BinaryPrimitives.WriteUInt16LittleEndian(row, (ushort)index);
            // A password and a card the till never keeps.
            Encoding.ASCII.GetBytes("1234").CopyTo(row[3..]);
            if (Modern)
            {
                Write(row.Slice(11, 24), name);
                BinaryPrimitives.WriteUInt32LittleEndian(row[35..], 99887766);
                Write(row.Slice(48, 24), number);
            }
            else
            {
                Write(row.Slice(8, 8), name);
                BinaryPrimitives.WriteUInt32LittleEndian(row[16..], 99887766);
                BinaryPrimitives.WriteUInt32LittleEndian(row[24..], uint.Parse(number, System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return table;
    }

    private byte[] AttendanceLog()
    {
        var size = Modern ? 40 : 16;
        var log = new byte[4 + size * Punches.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(log, (uint)(size * Punches.Count));
        for (var i = 0; i < Punches.Count; i++)
        {
            var (number, at, state, verify) = Punches[i];
            var row = log.AsSpan(4 + i * size, size);
            if (Modern)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(row, (ushort)Users.FirstOrDefault(u => u.Number == number).Index);
                Write(row.Slice(2, 24), number);
                row[26] = (byte)verify;
                BinaryPrimitives.WriteUInt32LittleEndian(row[27..], Encode(at));
                row[31] = (byte)state;
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(row, uint.Parse(number, System.Globalization.CultureInfo.InvariantCulture));
                BinaryPrimitives.WriteUInt32LittleEndian(row[4..], Encode(at));
                row[8] = (byte)verify;
                row[9] = (byte)state;
            }
        }

        return log;
    }

    private static void Write(Span<byte> field, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        bytes.AsSpan(0, Math.Min(bytes.Length, field.Length)).CopyTo(field);
    }

    public void Dispose() => _listener.Stop();
}
