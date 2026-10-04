using System.Net.Sockets;

namespace OtantikPos.Node.Infrastructure.Printing;

public sealed class TcpPrinterClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    // Raw TCP, port 9100. No driver, no spooler, nothing in between that can hang.
    //
    // Success means the printer accepted the bytes, not that paper came out. Plain ESC/POS
    // over 9100 sends no acknowledgement, and a printer that is out of paper usually still
    // takes the data. The ticket is then lost unless the kitchen asks for a reprint.
    // Confirming would need a status query (DLE EOT) after sending, which not every model
    // answers.
    public async Task SendAsync(PrinterOptions printer, byte[] content, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(printer.Host, printer.Port, timeout.Token);

        await using var stream = client.GetStream();
        await stream.WriteAsync(content, timeout.Token);
        await stream.FlushAsync(timeout.Token);
    }
}
