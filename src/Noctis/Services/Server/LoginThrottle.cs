using System.Collections.Concurrent;

namespace Noctis.Services.Server;

/// <summary>
/// Per-client brute-force brake for the server's login. A client (keyed by remote address)
/// that fails <see cref="MaxFailures"/> times within <see cref="Window"/> is locked out for
/// <see cref="Lockout"/>; a successful login clears its record. Cheap and in-memory: the
/// server is a home appliance, not a fleet, so a restart forgetting the counters is fine.
/// </summary>
public sealed class LoginThrottle
{
    public const int MaxFailures = 8;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan Lockout = TimeSpan.FromMinutes(15);

    private sealed class Entry
    {
        public readonly Queue<DateTime> Failures = new();
        public DateTime LockedUntil = DateTime.MinValue;
    }

    private readonly ConcurrentDictionary<string, Entry> _clients = new();
    private readonly Func<DateTime> _now;

    public LoginThrottle() : this(null) { }

    /// <summary>Clock injection for tests; null uses UTC now.</summary>
    public LoginThrottle(Func<DateTime>? now) => _now = now ?? (static () => DateTime.UtcNow);

    /// <summary>True while <paramref name="client"/> is locked out; <paramref name="retryAfter"/> says for how long.</summary>
    public bool IsLocked(string client, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (!_clients.TryGetValue(client, out var e)) return false;
        lock (e)
        {
            var now = _now();
            if (e.LockedUntil > now) { retryAfter = e.LockedUntil - now; return true; }
            return false;
        }
    }

    /// <summary>Records a failed login. Returns true when this failure triggered a lockout.</summary>
    public bool RecordFailure(string client)
    {
        var e = _clients.GetOrAdd(client, _ => new Entry());
        lock (e)
        {
            var now = _now();
            e.Failures.Enqueue(now);
            while (e.Failures.Count > 0 && now - e.Failures.Peek() > Window) e.Failures.Dequeue();
            if (e.Failures.Count < MaxFailures) return false;
            e.LockedUntil = now + Lockout;
            e.Failures.Clear();
            return true;
        }
    }

    /// <summary>A successful login wipes the client's slate.</summary>
    public void RecordSuccess(string client) => _clients.TryRemove(client, out _);

    /// <summary>Drops stale entries so the table cannot grow without bound (call occasionally).</summary>
    public void Prune()
    {
        var now = _now();
        foreach (var (key, e) in _clients)
        {
            lock (e)
            {
                if (e.LockedUntil <= now && (e.Failures.Count == 0 || now - e.Failures.Peek() > Window))
                    _clients.TryRemove(key, out _);
            }
        }
    }
}
