namespace OtantikPos.Node.Infrastructure.DeliverySystem;

public enum DeliverySystemLinkState
{
    // DeliverySystem:BaseUrl is empty: a standalone till.
    NotConfigured,

    // Starting up, before the first attempt has succeeded or failed.
    Connecting,

    Online,

    // Lost or never reached. The till keeps selling; what needs the cloud waits.
    Offline,
}

public sealed record DeliverySystemLinkStatus(DeliverySystemLinkState State, DateTime SinceUtc)
{
    // Customer lookup, applying and redeeming points: the cloud is the record for customers.
    public bool CloudFeaturesAvailable => State == DeliverySystemLinkState.Online;
}

// Whether this machine can reach the delivery system right now, as its SignalR connection to the
// cloud sees it. That connection's heartbeat is also how the cloud knows the restaurant is open
// for captains' orders (see CloudOrderListener), so the till and the cloud agree on "offline".
//
// The tills show it, and switch off what needs the cloud while it is not Online, rather than
// letting a cashier find out by a request failing.
public sealed class DeliverySystemLink
{
    private readonly Lock _gate = new();
    private DeliverySystemLinkStatus _current = new(DeliverySystemLinkState.Connecting, DateTime.UtcNow);

    public DeliverySystemLinkStatus Current
    {
        get { lock (_gate) return _current; }
    }

    // Raised on a real change only, outside the lock.
    public event Action<DeliverySystemLinkStatus>? Changed;

    public void Set(DeliverySystemLinkState state)
    {
        DeliverySystemLinkStatus changed;
        lock (_gate)
        {
            if (_current.State == state)
                return;
            changed = _current = new DeliverySystemLinkStatus(state, DateTime.UtcNow);
        }
        Changed?.Invoke(changed);
    }
}
