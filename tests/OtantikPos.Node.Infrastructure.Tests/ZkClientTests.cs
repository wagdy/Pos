using OtantikPos.Node.Infrastructure.Hr.ZkTeco;

namespace OtantikPos.Node.Infrastructure.Tests;

// ZKTeco's own protocol, against a fake device: the frames as the open-source client (pyzk) makes
// them, a comm key, and the user table and attendance log in both record sizes, through the
// device's buffer in more than one chunk.
public class ZkClientTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void Its_packets_are_pyzks_byte_for_byte()
    {
        // The first thing pyzk sends: connect, session 0, reply number 65534 counted on to 0.
        var connect = ZkClient.Packet(1000, [], 0, ushort.MaxValue - 1, out var next);
        Assert.Equal("50-50-82-7D-08-00-00-00-E8-03-17-FC-00-00-00-00", BitConverter.ToString(connect));
        Assert.Equal(0, next);

        // pyzk's make_commkey(12345, 0x2A17) and its packed time for 2026-10-21 16:03:09.
        Assert.Equal("6D-E1-32-53", BitConverter.ToString(ZkClient.CommKey(12345, 0x2A17)));
        Assert.Equal(new DateTime(2026, 10, 21, 16, 3, 9), ZkClient.Time([0x3D, 0x3E, 0x5A, 0x33]));
    }

    [Fact]
    public async Task It_reads_a_K40s_users_and_its_whole_log_through_the_buffer()
    {
        using var device = new FakeZkDevice();
        device.Users.Add((1, "7", "Hassan"));
        device.Users.Add((2, "12", "منى"));
        // More than one chunk of 0xFFC0 bytes: 2,000 punches of 40.
        var start = new DateTime(2026, 9, 1, 8, 0, 0);
        device.Punches.AddRange(Enumerable.Range(0, 2_000).Select(i => (i % 2 == 0 ? "7" : "12", start.AddMinutes(i * 7), i % 2, 1)));

        await using var zk = await ZkClient.ConnectAsync("127.0.0.1", device.Port, 0, Timeout, Cancel);
        Assert.Equal("CQZ7232060112", await zk.OptionAsync("~SerialNumber", Cancel));
        Assert.Equal("K40 Pro", await zk.OptionAsync("~DeviceName", Cancel));
        Assert.Null(await zk.OptionAsync("~NoSuchOption", Cancel));
        Assert.Equal("Ver 6.60 Apr 28 2017", await zk.FirmwareAsync(Cancel));
        var (users, records) = await zk.SizesAsync(Cancel);
        Assert.Equal((2, 2_000), (users, records));

        var table = await zk.UsersAsync(users, Cancel);
        Assert.Equal([new ZkUser(1, "7", "Hassan"), new ZkUser(2, "12", "منى")], table);
        var log = await zk.AttendanceAsync(records, table, Cancel);
        Assert.Equal(2_000, log.Count);
        Assert.Equal(new ZkPunch("7", start, 0, 1), log[0]);
        Assert.Equal(new ZkPunch("12", start.AddMinutes(1_999 * 7), 1, 1), log[^1]);
        // The users in one read of the buffer, the log in two.
        Assert.Equal(3, device.Commands.Count(c => c == 1504));
    }

    [Fact]
    public async Task An_older_devices_smaller_records_read_the_same()
    {
        using var device = new FakeZkDevice { Modern = false, FrameSize = 10 };
        device.Users.Add((3, "21", "Ali"));
        device.Punches.Add(("21", new DateTime(2026, 10, 1, 9, 30, 0), 0, 15));

        await using var zk = await ZkClient.ConnectAsync("127.0.0.1", device.Port, 0, Timeout, Cancel);
        var table = await zk.UsersAsync(1, Cancel);
        Assert.Equal([new ZkUser(3, "21", "Ali")], table);
        Assert.Equal([new ZkPunch("21", new DateTime(2026, 10, 1, 9, 30, 0), 0, 15)], await zk.AttendanceAsync(1, table, Cancel));
    }

    [Fact]
    public async Task A_comm_key_set_on_the_device_is_given_and_a_wrong_one_refused()
    {
        using var device = new FakeZkDevice { CommKey = 12345 };

        await using (var zk = await ZkClient.ConnectAsync("127.0.0.1", device.Port, 12345, Timeout, Cancel))
            Assert.Equal("CQZ7232060112", await zk.OptionAsync("~SerialNumber", Cancel));
        var refused = await Assert.ThrowsAsync<ZkException>(() => ZkClient.ConnectAsync("127.0.0.1", device.Port, 1, Timeout, Cancel));
        Assert.Contains("comm key", refused.Message);
        Assert.Contains((ushort)1102, device.Commands);
    }

    [Fact]
    public async Task A_device_that_stops_answering_is_let_go()
    {
        using var device = new FakeZkDevice { Silent = true };

        var silent = await Assert.ThrowsAsync<ZkException>(() => ZkClient.ConnectAsync("127.0.0.1", device.Port, 0, TimeSpan.FromMilliseconds(300), Cancel));
        Assert.Contains("stopped answering", silent.Message);
    }
}
