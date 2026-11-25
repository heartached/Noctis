using System.Diagnostics;
using Noctis.Models;

namespace Noctis.Services.AudioAnalysis;

/// <summary>How much silence a song file has before its first sound and after its last one.</summary>
public sealed record SilenceBounds(TimeSpan Leading, TimeSpan Trailing)
{
    public static readonly SilenceBounds None = new(TimeSpan.Zero, TimeSpan.Zero);
}

/// <summary>
/// The pure part of Skip Silence (Discord, Spark 11-23): where the sound starts and ends in a
/// run of per-window peaks. A window is "sound" when its peak reaches the threshold.
/// </summary>
public static class SilenceEdges
{
    /// <summary>Length of one peak window.</summary>
    public const double WindowSeconds = 0.01;

    /// <summary>Kept before the first sound, so a soft attack isn't clipped.</summary>
    public static readonly TimeSpan LeadPad = TimeSpan.FromMilliseconds(60);

    /// <summary>Kept after the last sound, so a fade's final bit and the reverb tail stay.</summary>
    public static readonly TimeSpan TrailPad = TimeSpan.FromMilliseconds(250);

    /// <summary>Shorter leading silence is left alone: a seek costs more than it saves.</summary>
    public static readonly TimeSpan MinLeading = TimeSpan.FromMilliseconds(400);

    /// <summary>Shorter trailing silence is left alone: ending early opens the next song
    /// cold, which is only worth it when there is real silence to cut.</summary>
    public static readonly TimeSpan MinTrailing = TimeSpan.FromSeconds(1);

    public static float ThresholdToLinear(double thresholdDb) => (float)Math.Pow(10, thresholdDb / 20.0);

    /// <summary>Silence before the first window at or above <paramref name="threshold"/>,
    /// minus <see cref="LeadPad"/>; zero when the stretch holds no sound at all (unknown, so
    /// nothing is cut) or the silence is shorter than <see cref="MinLeading"/>.</summary>
    public static TimeSpan Leading(ReadOnlySpan<float> peaks, float threshold)
    {
        for (var i = 0; i < peaks.Length; i++)
        {
            if (peaks[i] < threshold) continue;
            var silence = TimeSpan.FromSeconds(i * WindowSeconds) - LeadPad;
            return silence >= MinLeading ? silence : TimeSpan.Zero;
        }
        return TimeSpan.Zero;
    }

    /// <summary>Silence after the last window at or above <paramref name="threshold"/>, measured
    /// to the end of <paramref name="peaks"/> (which must run to the end of the file), minus
    /// <see cref="TrailPad"/>; zero when there is no sound or too little silence.</summary>
    public static TimeSpan Trailing(ReadOnlySpan<float> peaks, float threshold)
    {
        for (var i = peaks.Length - 1; i >= 0; i--)
        {
            if (peaks[i] < threshold) continue;
            var silence = TimeSpan.FromSeconds((peaks.Length - 1 - i) * WindowSeconds) - TrailPad;
            return silence >= MinTrailing ? silence : TimeSpan.Zero;
        }
        return TimeSpan.Zero;
    }
}

/// <summary>
/// Skip Silence (Discord, Spark 11-23: "many of my songs have unnecessary blank starts and
/// ends"): finds the silence at both ends of a song by decoding its first and last
/// <see cref="EdgeSeconds"/> seconds with ffmpeg (or libVLC without it), out of process and at below-normal priority
/// (the same idiom as the BPM/key analysis). Files are never changed. Results are kept in
/// memory per file (size + modified time) and threshold, so a song is scanned once per session.
/// </summary>
public sealed class SilenceScanService
{
    /// <summary>How much of each end is decoded. Silence longer than this is not cut (the
    /// stretch then holds no sound, so nothing is known about where it ends).</summary>
    public const int EdgeSeconds = 30;

    private const int SampleRate = 22050;
    private const int Channels = 2;
    private static readonly TimeSpan DecodeTimeout = TimeSpan.FromSeconds(30);
    private const int MaxEntries = 4096;

    private readonly Func<string?> _ffmpegPath;
    // Without ffmpeg (Linux builds don't bundle it): libVLC, which every build ships.
    private readonly Func<string, TimeSpan, bool, CancellationToken, Task<List<float>?>>? _fallbackDecoder;
    private readonly object _gate = new();
    private readonly Dictionary<string, SilenceBounds> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly Dictionary<string, Task<SilenceBounds>> _inFlight = new(StringComparer.Ordinal);
    // One ffmpeg at a time: the scans are tiny, and a burst (a queue change) must not fan out.
    private readonly SemaphoreSlim _decodeGate = new(1, 1);

    public SilenceScanService(Func<string?> ffmpegPath,
        Func<string, TimeSpan, bool, CancellationToken, Task<List<float>?>>? fallbackDecoder = null)
    {
        _ffmpegPath = ffmpegPath;
        _fallbackDecoder = fallbackDecoder;
    }

    /// <summary>The app's own decoder: libVLC writing the song's ends to a temporary WAV.</summary>
    public static Task<List<float>?> DecodeWithVlc(string path, TimeSpan duration, bool fromEnd, CancellationToken ct) =>
        VlcEdgeDecoder.DecodePeaksAsync(path, duration, fromEnd, EdgeSeconds, ct);

    public bool IsAvailable => _ffmpegPath() != null || _fallbackDecoder != null;

    /// <summary>A file the scan can read: a local song file that exists.</summary>
    public static bool CanScan(Track track) =>
        track.SourceType == SourceType.Local &&
        !track.IsRemoteStream &&
        !string.IsNullOrWhiteSpace(track.FilePath) &&
        !VlcAudioPlayer.IsPathlessMedia(track.FilePath) &&
        track.Duration > TimeSpan.FromSeconds(3);

    /// <summary>Scans <paramref name="track"/> (or joins the scan already running for it), off
    /// the calling thread: even the file check runs in the background, so a sleeping disk never
    /// stalls the UI. A file that can't be read gives <see cref="SilenceBounds.None"/>, cached
    /// like any result, so it isn't retried for the rest of the session.</summary>
    /// <param name="urgent">The song that just started: it doesn't wait behind scans made
    /// ahead of time (one of those on a sleeping drive held a started song's scan until its
    /// leading silence had already played).</param>
    public Task<SilenceBounds> ScanAsync(Track track, double thresholdDb, bool urgent = false, CancellationToken ct = default)
    {
        if (!CanScan(track)) return Task.FromResult(SilenceBounds.None);
        var path = track.FilePath;
        var flightKey = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{path}|{Math.Round(thresholdDb)}");
        lock (_gate)
        {
            if (_inFlight.TryGetValue(flightKey, out var running)) return running;
            var duration = track.Duration;
            var task = Task.Run(() => RunScanAsync(path, duration, flightKey, thresholdDb, urgent, ct), ct);
            _inFlight[flightKey] = task;
            return task;
        }
    }

    private async Task<SilenceBounds> RunScanAsync(string path, TimeSpan duration, string flightKey, double thresholdDb, bool urgent, CancellationToken ct)
    {
        try
        {
            var key = KeyFor(path, thresholdDb);
            if (key == null) return SilenceBounds.None;
            lock (_gate)
            {
                if (_cache.TryGetValue(key, out var hit)) return hit;
            }

            var result = SilenceBounds.None;
            try
            {
                var ffmpeg = _ffmpegPath();
                if (ffmpeg != null || _fallbackDecoder != null)
                {
                    if (!urgent) await _decodeGate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        var threshold = SilenceEdges.ThresholdToLinear(thresholdDb);
                        var sw = Stopwatch.StartNew();
                        var head = ffmpeg != null
                            ? await DecodePeaksAsync(ffmpeg, path, fromEnd: false, ct).ConfigureAwait(false)
                            : await _fallbackDecoder!(path, duration, false, ct).ConfigureAwait(false);
                        var tail = ffmpeg != null
                            ? await DecodePeaksAsync(ffmpeg, path, fromEnd: true, ct).ConfigureAwait(false)
                            : await _fallbackDecoder!(path, duration, true, ct).ConfigureAwait(false);
                        result = new SilenceBounds(
                            head is null ? TimeSpan.Zero : SilenceEdges.Leading(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(head), threshold),
                            tail is null ? TimeSpan.Zero : SilenceEdges.Trailing(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(tail), threshold));
                        DebugLogger.Info(DebugLogger.Category.Playback, "SkipSilence.Scan",
                            $"file={Path.GetFileName(path)}, decoder={(ffmpeg != null ? "ffmpeg" : "vlc")}, thresholdDb={thresholdDb:0}, leadingMs={result.Leading.TotalMilliseconds:F0}, " +
                            $"trailingMs={result.Trailing.TotalMilliseconds:F0}, headWindows={head?.Count ?? -1}, tailWindows={tail?.Count ?? -1}, ms={sw.ElapsedMilliseconds}");
                    }
                    finally
                    {
                        if (!urgent) _decodeGate.Release();
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A timeout or an unreadable file: nothing is cut, and it isn't tried again.
                DebugLogger.Warn(DebugLogger.Category.Playback, "SkipSilence.ScanFailed", $"file={Path.GetFileName(path)}, {ex.GetType().Name}: {ex.Message}");
            }

            lock (_gate)
            {
                if (!_cache.ContainsKey(key))
                {
                    _cache[key] = result;
                    _order.Enqueue(key);
                    while (_order.Count > MaxEntries && _order.TryDequeue(out var old))
                        _cache.Remove(old);
                }
            }
            return result;
        }
        finally
        {
            lock (_gate) _inFlight.Remove(flightKey);
        }
    }

    /// <summary>Per-window peaks (<see cref="SilenceEdges.WindowSeconds"/>) of the first, or the
    /// last, <see cref="EdgeSeconds"/> seconds; null when ffmpeg fails or decodes nothing.</summary>
    internal static async Task<List<float>?> DecodePeaksAsync(string ffmpeg, string source, bool fromEnd, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var edge = EdgeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        psi.ArgumentList.Add("-nostats");
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        // -sseof (input option): start this far before the end; a shorter file starts at 0.
        // The decode always runs to the end of the file, so the trailing silence is measured
        // from the real end even where the seek itself lands a little off.
        psi.ArgumentList.Add(fromEnd ? "-sseof" : "-t");
        psi.ArgumentList.Add(fromEnd ? "-" + edge : edge);
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(source);
        foreach (var a in new[] { "-map", "0:a:0", "-vn", "-ac", Channels.ToString(), "-ar", SampleRate.ToString(), "-f", "f32le", "-" })
            psi.ArgumentList.Add(a);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DecodeTimeout);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg start failed");
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* exited / not permitted */ }
        using var reg = timeout.Token.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } });

        var stderrTask = p.StandardError.ReadToEndAsync();
        var acc = new PeakAccumulator((int)Math.Round(SampleRate * SilenceEdges.WindowSeconds) * Channels);
        var buffer = new byte[64 * 1024];
        var stdout = p.StandardOutput.BaseStream;
        var carry = 0;
        while (true)
        {
            var n = await stdout.ReadAsync(buffer.AsMemory(carry, buffer.Length - carry), timeout.Token).ConfigureAwait(false);
            if (n == 0) break;
            var total = carry + n;
            var whole = total / 4 * 4;
            acc.Add(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, whole)));
            carry = total - whole;
            if (carry > 0) Buffer.BlockCopy(buffer, whole, buffer, 0, carry);
        }
        await stderrTask.ConfigureAwait(false);
        await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var peaks = acc.Finish();
        return peaks.Count == 0 ? null : peaks;
    }

    /// <summary>Folds interleaved samples into one peak (largest |sample| over all channels)
    /// per window.</summary>
    internal sealed class PeakAccumulator
    {
        private readonly int _samplesPerWindow;
        private readonly List<float> _peaks = new();
        private float _peak;
        private int _count;

        public PeakAccumulator(int samplesPerWindow) => _samplesPerWindow = Math.Max(1, samplesPerWindow);

        public void Add(ReadOnlySpan<float> samples)
        {
            foreach (var s in samples)
            {
                var a = Math.Abs(s);
                if (a > _peak || float.IsNaN(_peak)) _peak = a;
                if (++_count < _samplesPerWindow) continue;
                _peaks.Add(_peak);
                _peak = 0;
                _count = 0;
            }
        }

        /// <summary>The peaks, with a final partial window counted as a whole one.</summary>
        public List<float> Finish()
        {
            if (_count > 0) _peaks.Add(_peak);
            _peak = 0;
            _count = 0;
            return _peaks;
        }
    }

    private static string? KeyFor(string? path, double thresholdDb)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{Math.Round(thresholdDb)}");
        }
        catch
        {
            return null;
        }
    }
}
