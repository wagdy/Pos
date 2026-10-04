namespace OtantikPos.Node.Infrastructure.Common;

public static class Backoff
{
    // 2s, 4s, 8s ... up to `max`. Retries go on for as long as it takes; the cap only stops
    // the gaps growing so long that a fixed fault (a printer switched back on, a database that
    // restarted) waits ages to be noticed.
    public static TimeSpan After(int attempts, TimeSpan max) =>
        TimeSpan.FromSeconds(Math.Min(Math.Pow(2, Math.Min(attempts, 20)), max.TotalSeconds));
}
