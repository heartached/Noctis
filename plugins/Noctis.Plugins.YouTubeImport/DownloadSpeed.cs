using System.Globalization;

namespace Noctis.Plugins.YouTubeImport;

/// <summary>A byte-level download report: what <see cref="DownloadSpeedTracker"/> sampled.</summary>
public sealed record DownloadReport(long DoneBytes, long TotalBytes, double BytesPerSec, TimeSpan? Eta);

/// <summary>One yt-dlp <c>[download]</c> line's numbers: percent always, speed/ETA when present.</summary>
public sealed record DownloadDetail(double Percent, double? BytesPerSec, TimeSpan? Eta);

/// <summary>
/// Formats a live download for the status line: "4.2 MB/s · about 30 s left". Same units and
/// rounding as the app's own downloads (Lyrics Studio, Settings → Plugins: DownloadMeter), in
/// English like the rest of this plugin (plugins have no access to the app's translations).
/// </summary>
public static class DownloadFormat
{
    /// <summary>"4.2 MB/s · about 30 s left", or just "4.2 MB/s" without an ETA.</summary>
    public static string Format(double bytesPerSec, TimeSpan? eta)
        => eta is null ? Rate(bytesPerSec) : $"{Rate(bytesPerSec)} · {FormatEta(eta.Value)}";

    /// <summary>"850 KB/s", "4.2 MB/s", "12 MB/s".</summary>
    public static string Rate(double bytesPerSec)
    {
        var c = CultureInfo.InvariantCulture;
        var mb = bytesPerSec / (1 << 20);
        if (mb >= 10) return mb.ToString("0", c) + " MB/s";
        if (mb >= 1) return mb.ToString("0.0", c) + " MB/s";
        return Math.Max(1, bytesPerSec / 1024).ToString("0", c) + " KB/s";
    }

    /// <summary>"about 40 s left" / "about 3 min left" / "about 1 h 5 min left": rounded so it does not flicker.</summary>
    public static string FormatEta(TimeSpan eta)
    {
        if (eta < TimeSpan.Zero) eta = TimeSpan.Zero;
        if (eta.TotalSeconds < 50)
            return $"about {Math.Max(5, (int)Math.Ceiling(eta.TotalSeconds / 5) * 5)} s left";
        if (eta.TotalMinutes < 59.5)
            return $"about {Math.Max(1, (int)Math.Round(eta.TotalMinutes, MidpointRounding.AwayFromZero))} min left";
        var minutes = (int)Math.Round(eta.TotalMinutes, MidpointRounding.AwayFromZero);
        return $"about {minutes / 60} h {minutes % 60} min left";
    }
}

/// <summary>
/// Bytes/s + ETA from byte samples over a sliding ~3 s window (not thread-safe: sample from the
/// one loop that reads the download). Returns null until two samples span at least half a
/// second with bytes moving, so the status line keeps its plain "Downloading…" until the rate is real.
/// </summary>
public sealed class DownloadSpeedTracker
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Warmup = TimeSpan.FromMilliseconds(500);

    private readonly Func<DateTime> _clock;
    private readonly Queue<(DateTime At, long Done)> _samples = new();

    public DownloadSpeedTracker(Func<DateTime>? clock = null) => _clock = clock ?? (() => DateTime.UtcNow);

    /// <summary>Null while warming up, stalled, or done (nothing left to estimate).</summary>
    public (double BytesPerSec, TimeSpan? Eta)? Sample(long doneBytes, long totalBytes)
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
        TimeSpan? eta = null;
        if (totalBytes > 0 && doneBytes < totalBytes)
            eta = TimeSpan.FromSeconds((totalBytes - doneBytes) / bytesPerSec);
        return (bytesPerSec, eta);
    }
}

/// <summary>
/// At most one progress report per <see cref="Interval"/> (~4 a second): a 64 KiB read loop or
/// yt-dlp's progress lines otherwise post a report, and a status string, many times a second.
/// </summary>
internal sealed class ReportThrottle
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    private readonly Func<DateTime> _clock;
    private DateTime _last = DateTime.MinValue;

    public ReportThrottle(Func<DateTime>? clock = null) => _clock = clock ?? (() => DateTime.UtcNow);

    /// <summary>True (and the clock restarts) when a report is due.</summary>
    public bool Due()
    {
        var now = _clock();
        if (now - _last < Interval) return false;
        _last = now;
        return true;
    }
}

/// <summary>Forwards each report on the caller's thread, in order (unlike <see cref="Progress{T}"/>, which posts).</summary>
internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
