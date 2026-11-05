using System.Globalization;

namespace Noctis.Plugins.YouTubeImport;

/// <summary>A byte-level download report: what <see cref="DownloadSpeedTracker"/> sampled.</summary>
public sealed record DownloadReport(long DoneBytes, long TotalBytes, double MegabitsPerSec, TimeSpan? Eta);

/// <summary>One yt-dlp <c>[download]</c> line's numbers: percent always, speed/ETA when present.</summary>
public sealed record DownloadDetail(double Percent, double? MegabitsPerSec, TimeSpan? Eta);

/// <summary>Formats a live download for the status line: "12.4 Mbit/s · 32 min 12 s left".</summary>
public static class DownloadFormat
{
    /// <summary>"12.4 Mbit/s · 32 s left", or just "12.4 Mbit/s" without an ETA.</summary>
    public static string Format(double megabitsPerSec, TimeSpan? eta)
    {
        var speed = megabitsPerSec < 100
            ? megabitsPerSec.ToString("0.0", CultureInfo.InvariantCulture)
            : Math.Round(megabitsPerSec).ToString("0", CultureInfo.InvariantCulture);
        return eta is null ? $"{speed} Mbit/s" : $"{speed} Mbit/s · {FormatEta(eta.Value)}";
    }

    /// <summary>"1 s left", "32 min 12 s left", "1 h 2 min left".</summary>
    public static string FormatEta(TimeSpan eta)
    {
        if (eta < TimeSpan.Zero) eta = TimeSpan.Zero;
        if (eta.TotalSeconds < 60) return $"{Math.Max(1, (int)Math.Round(eta.TotalSeconds))} s left";
        if (eta.TotalHours < 1) return $"{(int)eta.TotalMinutes} min {eta.Seconds} s left";
        return $"{(int)eta.TotalHours} h {eta.Minutes} min left";
    }
}

/// <summary>
/// Mbit/s + ETA from byte samples over a sliding ~3 s window (UI thread or worker: not thread-safe).
/// Returns null until two samples span at least half a second with bytes moving, so the status
/// line keeps its plain "Downloading…" until the rate is real.
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
