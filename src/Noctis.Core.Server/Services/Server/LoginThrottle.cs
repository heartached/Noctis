using System.Collections.Concurrent;

namespace Noctis.Services.Server;

/// <summary>
/// Per-client brute-force brake for the server's login. A client (keyed by remote address and
/// account name) that fails <see cref="MaxFailures"/> times within <see cref="Window"/> is locked
/// out for <see cref="Lockout"/>; a successful login clears its record. Cheap and in-memory: the
/// server is a home appliance, not a fleet, so a restart forgetting the counters is fine.
/// </summary>
public sealed class LoginThrottle
{
    public const int MaxFailures = 8;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan Lockout = TimeSpan.FromMinutes(15);

    /// <summary>Most entries kept; past it a new name's failures count against its address.</summary>
    public const int MaxEntries = 10_000;

    /// <summary>ServerUserStore's name limit: a longer name cannot be an account.</summary>
    public const int MaxNameLength = 64;

    private sealed class Entry
    {
        public readonly Queue<DateTime> Failures = new();
        public DateTime LockedUntil = DateTime.MinValue;
    }

    private readonly ConcurrentDictionary<string, Entry> _clients = new();
    private readonly Func<DateTime> _now;
    private DateTime _nextPrune = DateTime.MinValue;

    public LoginThrottle() : this(null) { }

    /// <summary>Clock injection for tests; null uses UTC now.</summary>
    public LoginThrottle(Func<DateTime>? now) => _now = now ?? (static () => DateTime.UtcNow);

    /// <summary>
    /// The key for a login as <paramref name="name"/> from <paramref name="client"/>. Names no
    /// account can have share one bucket per address, so a huge junk name (a form POST allows
    /// about 1 MB) is not kept in the table.
    /// </summary>
    public static string Key(string client, string name)
    {
        name = name.Trim();
        return client + "\n" + (name.Length > MaxNameLength ? "\0" : name.ToLowerInvariant());
    }

    // The address part of a Key (the whole string for a plain address key).
    private static string AddressOf(string key)
    {
        var cut = key.IndexOf('\n');
        return cut < 0 ? key : key[..cut];
    }

    /// <summary>True while <paramref name="client"/> (or its address, see <see cref="MaxEntries"/>) is locked out; <paramref name="retryAfter"/> says for how long.</summary>
    public bool IsLocked(string client, out TimeSpan retryAfter)
    {
        if (IsLockedKey(client, out retryAfter)) return true;
        var address = AddressOf(client);
        return address != client && IsLockedKey(address, out retryAfter);
    }

    private bool IsLockedKey(string client, out TimeSpan retryAfter)
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
        // Keys include the account name the client sent, so junk names must not pile up
        // forever: sweep stale entries at most once per window.
        var start = _now();
        if (start >= _nextPrune) { _nextPrune = start + Window; Prune(); }

        // Table full (a flood of distinct names): count a new name against its address, which
        // IsLocked also checks, so the flood adds no entries and still trips the lockout.
        if (!_clients.ContainsKey(client) && _clients.Count >= MaxEntries) client = AddressOf(client);

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

    internal int Count => _clients.Count;

    /// <summary>Drops stale entries so the table cannot grow without bound (call occasionally).</summary>
    public void Prune()
    {
        var now = _now();
        foreach (var (key, e) in _clients)
        {
            lock (e)
            {
                // Drop only failures that left the window; the entry goes once none are left.
                while (e.Failures.Count > 0 && now - e.Failures.Peek() > Window) e.Failures.Dequeue();
                if (e.LockedUntil <= now && e.Failures.Count == 0)
                    _clients.TryRemove(key, out _);
            }
        }
    }
}
