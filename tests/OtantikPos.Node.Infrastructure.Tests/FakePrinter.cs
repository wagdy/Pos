using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OtantikPos.Node.Infrastructure.Tests;

// A network thermal printer as far as the till can tell: something on a TCP port that takes
// bytes. Keeps what it receives as text (Latin-1, so ESC/POS control bytes stay one character).
public sealed class FakePrinter : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

    public ConcurrentQueue<string> Printed { get; } = new();

    public FakePrinter()
    {
        _listener.Start();
        _ = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync();
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        var buffer = new MemoryStream();
                        await client.GetStream().CopyToAsync(buffer);
                        Printed.Enqueue(Encoding.Latin1.GetString(buffer.ToArray()));
                    }
                });
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }
    }

    public void Dispose() => _listener.Stop();
}
