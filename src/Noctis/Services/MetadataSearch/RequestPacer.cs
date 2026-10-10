using System.Diagnostics;

namespace Noctis.Services.MetadataSearch;

/// <summary>
/// Spaces outgoing requests to one service: a minimum gap between request starts plus an
/// optional cap per sliding window. Callers queue in order. One instance per remote service,
/// shared by every code path that calls it — two independent 1 req/s gates against MusicBrainz
/// would add up to 2 req/s and MusicBrainz then rejects ALL of this IP's requests with 503.
/// </summary>
public sealed class RequestPacer
{
    // MusicBrainz: "about 1 request per second on average per IP"; over that, every request is
    // declined (musicbrainz.org/doc/MusicBrainz_API/Rate_Limiting). 1.1 s keeps a margin.
    public static readonly RequestPacer MusicBrainz = new(TimeSpan.FromMilliseconds(1100));

    // Deezer's public API quota is 50 requests / 5 s per IP (error code 4 "Quota limit exceeded").
    // Stay under it with headroom for the rest of the app (artist images, the old finder).
    public static readonly RequestPacer Deezer = new(TimeSpan.FromMilliseconds(60), 40, TimeSpan.FromSeconds(5));

    // Apple documents the iTunes Search API as "approximately 20 calls per minute".
    public static readonly RequestPacer AppleMusic = new(TimeSpan.FromMilliseconds(250), 20, TimeSpan.FromMinutes(1));

    // Cover Art Archive documents no limit; a small gap keeps bursts polite.
    public static readonly RequestPacer CoverArtArchive = new(TimeSpan.FromMilliseconds(100));

    private readonly TimeSpan _minInterval;
    private readonly int _maxPerWindow;
    private readonly TimeSpan _window;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<long> _recent = new();
    private long _last = long.MinValue;

    public RequestPacer(TimeSpan minInterval, int maxPerWindow = 0, TimeSpan window = default)
    {
        _minInterval = minInterval;
        _maxPerWindow = maxPerWindow;
        _window = window;
    }

    public TimeSpan MinInterval => _minInterval;

    /// <summary>Waits until a request may start, then claims that slot.</summary>
    public async Task WaitAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var now = Stopwatch.GetTimestamp();
                var wait = TimeSpan.Zero;
                if (_last != long.MinValue)
                {
                    var since = Stopwatch.GetElapsedTime(_last, now);
                    if (since < _minInterval) wait = _minInterval - since;
                }
                if (_maxPerWindow > 0)
                {
                    while (_recent.Count > 0 && Stopwatch.GetElapsedTime(_recent.Peek(), now) >= _window)
                        _recent.Dequeue();
                    if (_recent.Count >= _maxPerWindow)
                    {
                        var free = _window - Stopwatch.GetElapsedTime(_recent.Peek(), now);
                        if (free > wait) wait = free;
                    }
                }
                if (wait <= TimeSpan.Zero) break;
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }

            _last = Stopwatch.GetTimestamp();
            if (_maxPerWindow > 0) _recent.Enqueue(_last);
        }
        finally
        {
            _gate.Release();
        }
    }
}
