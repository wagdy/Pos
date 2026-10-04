using System.Threading.Channels;

namespace OtantikPos.Node.Infrastructure.Common;

// Lets a background worker sleep until there is work, instead of waking on a timer. The
// outbox and the print worker each keep a short poll as a backstop, but without this a
// kitchen ticket would sit in the queue until the next poll came round.
//
// Any number of Notify calls before the worker wakes collapse into one wake-up. The worker
// drains everything that is due each time it wakes, so nothing is lost.
public abstract class WakeSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Notify() => _channel.Writer.TryWrite(true);

    // Returns when notified or when `timeout` passes, whichever comes first.
    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await _channel.Reader.ReadAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }
}

public sealed class OutboxSignal : WakeSignal;

public sealed class PrintSignal : WakeSignal;
