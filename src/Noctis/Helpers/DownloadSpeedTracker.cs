namespace Noctis.Helpers;

/// <summary>
/// Mbit/s + ETA from byte samples over a sliding ~3 s window. Returns null until two samples
/// span at least half a second with bytes moving, so the status line keeps its plain text
/// until the rate is real. Single-threaded: sample wherever the progress callback runs.
/// </summary>
public sealed class DownloadSpeedTracker
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Warmup = TimeSpan.FromMilliseconds(500);

    private readonly Func<DateTime> _clock;
    private readonly Queue<(DateTime At, long Done)> _samples = new();

    public DownloadSpeedTracker(Func<DateTime>? clock = null) => _clock = clock ?? (() => DateTime.UtcNow);

    /// <summary>Null while warming up, stalled, or done (nothing left to estimate).</summary>
    public (double MegabitsPerSec, TimeSpan? Eta)? Sample(long doneBytes, long totalBytes)
    {
        var now = _clock();
        _samples.Enqueue((now, doneBytes));
        while (_samples.Count > 0 && now - _samples.Peek().At > Window) _samples.Dequeue();

        if (_samples.Count < 2) return null;
        var first = _samples.Peek();
        var span = now - first.At;
        var advanced = doneBytes - first.Done;
        if (span < Warmup || advanced <= 0) return null;

        var bytesPerSec = advanced / span.TotalSeconds;
        var mbps = bytesPerSec * 8 / 1_000_000;
        TimeSpan? eta = null;
        if (totalBytes > 0 && doneBytes < totalBytes)
            eta = TimeSpan.FromSeconds((totalBytes - doneBytes) / bytesPerSec);
        return (mbps, eta);
    }
}
