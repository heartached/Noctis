using System.Diagnostics;

namespace Noctis.Services.MetadataSearch;

/// <summary>
/// Small thread-safe LRU with a time-to-live, for the session's search results and detail
/// payloads: re-running a search or re-opening a candidate must not hit the network again
/// (MusicBrainz at 1 req/s makes every repeat cost seconds), but results must not live forever.
/// </summary>
public sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly TimeSpan _ttl;
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _map;
    private readonly LinkedList<Entry> _order = new();
    private readonly object _lock = new();

    private sealed record Entry(TKey Key, TValue Value, long StoredAt);

    public LruCache(int capacity, TimeSpan ttl, IEqualityComparer<TKey>? comparer = null)
    {
        _capacity = Math.Max(1, capacity);
        _ttl = ttl;
        _map = new Dictionary<TKey, LinkedListNode<Entry>>(comparer);
    }

    public int Count { get { lock (_lock) return _map.Count; } }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                if (Stopwatch.GetElapsedTime(node.Value.StoredAt) < _ttl)
                {
                    _order.Remove(node);
                    _order.AddFirst(node);
                    value = node.Value.Value;
                    return true;
                }
                _order.Remove(node);
                _map.Remove(key);
            }
            value = default!;
            return false;
        }
    }

    public void Set(TKey key, TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _order.Remove(existing);
                _map.Remove(key);
            }
            var node = _order.AddFirst(new Entry(key, value, Stopwatch.GetTimestamp()));
            _map[key] = node;
            while (_map.Count > _capacity && _order.Last is { } last)
            {
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _order.Clear();
        }
    }
}
