using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OtantikPos.Node.Infrastructure.Tests;

// A network thermal printer as far as the till can tell: something on a TCP port that takes
// bytes. Keeps what it receives as text (Latin-1, so ESC/POS control bytes stay one character).
public sealed class FakePrinter : IDisposable
{
    private TcpListener _listener = new(IPAddress.Loopback, 0);

    public ConcurrentQueue<string> Printed { get; } = new();

    public FakePrinter()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptAsync();
    }

    public int Port { get; }

    // Off, as a printer switched off or unplugged: connections are refused.
    public void SwitchOff() => _listener.Stop();

    // Back on, on the same port.
    public void SwitchOn()
    {
        _listener = new TcpListener(IPAddress.Loopback, Port);
        _listener.Start();
        _ = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        var listener = _listener;
        try
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync();
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
