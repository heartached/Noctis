using Noctis.Models;
using Noctis.Services;

namespace Noctis.Services.AudioAnalysis;

/// <summary>
/// Background driver that analyses tracks missing BPM/key. ThreadPool work, bounded
/// concurrency, cancellable, throttled. ffmpeg runs out-of-process so the UI thread and
/// playback (_playbackLock) are never blocked. Only fills fields that are missing; values
/// present from tags are preserved.
/// </summary>
public sealed class AudioAnalysisCoordinator
{
    private readonly IAudioAnalysisService _analysis;
    private readonly IAudioAnalysisStore _store;
    private readonly ILibraryService _library;
    private readonly Func<AppSettings> _settings;

    // Yield CPU between files so playback/UI stay responsive.
    private const int PerFileThrottleMs = 150;
    // Tag-write retry policy (same shape as ReplayGainScannerService).
    private const int TagWriteMaxAttempts = 3;
    private const int TagWriteRetryDelayMs = 150;

    private CancellationTokenSource? _cts;
    private Task? _run;
    private readonly object _startLock = new();

    public AudioAnalysisCoordinator(
        IAudioAnalysisService analysis,
        IAudioAnalysisStore store,
        ILibraryService library,
        Func<AppSettings> settings)
    {
        _analysis = analysis;
        _store = store;
        _library = library;
        _settings = settings;
    }

    public static bool NeedsAnalysis(Track t) =>
        t.Bpm <= 0 || string.IsNullOrWhiteSpace(t.MusicalKey);

    /// <summary>Starts a backfill pass over the current library if one is not already running.</summary>
    public void StartBackfill()
    {
        if (!_settings().BpmKeyAnalysisEnabled || !_analysis.IsAvailable) return;
        // LibraryUpdated fires in bursts during a scan's progressive publishes
        // (and from worker threads). An unsynchronized check-then-start here let
        // one burst spawn dozens of concurrent backfill loops, each re-analyzing
        // the same tracks and decoding ~10 MB of PCM at a time — the heap grew
        // by GBs within minutes. The check and start must be atomic.
        lock (_startLock)
        {
            if (_run is { IsCompleted: false }) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _run = Task.Run(() => RunAsync(token));
        }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // Snapshot so library mutations during the pass don't break enumeration.
        // StartBackfill is wired to LibraryUpdated, which can mutate the live track
        // list mid-enumeration; guard against "Collection was modified" by retrying
        // once, then bailing so a concurrent trigger can never fault this task.
        List<Track> pending;
        try
        {
            pending = SnapshotPending();
        }
        catch (InvalidOperationException)
        {
            try { pending = SnapshotPending(); }
            catch (InvalidOperationException) { return; }
        }

        bool anyWritten = false;
        foreach (var track in pending)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                if (!System.IO.File.Exists(track.FilePath)) continue;

                var info = new System.IO.FileInfo(track.FilePath);
                var sig = info.LastWriteTimeUtc.ToString("O");

                var cached = await _store.GetAsync(track.FilePath, ct);
                AudioAnalysisResult result;
                if (cached != null && cached.FileSize == info.Length && cached.LastModifiedUtc == sig)
                    result = new AudioAnalysisResult(cached.Bpm, cached.BpmConfidence, cached.MusicalKey, cached.KeyConfidence);
                else
                {
                    result = await _analysis.AnalyzeAsync(track.FilePath, ct);
                    if (result.Failed) continue;
                    await _store.UpsertAsync(new TrackAnalysisRecord(
                        track.FilePath, info.Length, sig, result.Bpm, result.BpmConfidence,
                        result.MusicalKey, result.KeyConfidence, DateTime.UtcNow.ToString("O")), ct);
                }

                bool changed = false;
                if (track.Bpm <= 0 && result.Bpm > 0) { track.Bpm = result.Bpm; changed = true; }
                if (string.IsNullOrWhiteSpace(track.MusicalKey) && !string.IsNullOrWhiteSpace(result.MusicalKey))
                { track.MusicalKey = result.MusicalKey; changed = true; }

                if (changed)
                {
                    anyWritten = true;
                    if (_settings().WriteAnalysisToTags) TryWriteTags(track);
                }

                // Throttle: yield CPU between files so playback/UI stay responsive.
                await Task.Delay(PerFileThrottleMs, ct);
            }
            catch (OperationCanceledException) { break; }
            catch { /* per-file failure: continue */ }
        }

        if (anyWritten)
        {
            try { await _library.SaveAsync(); } catch { }
        }
    }

    private List<Track> SnapshotPending() =>
        _library.Tracks
            .Where(t => t.SourceType == SourceType.Local && NeedsAnalysis(t))
            .ToList();

    private static void TryWriteTags(Track track)
    {
        for (int attempt = 0; attempt < TagWriteMaxAttempts; attempt++)
        {
            try
            {
                using var file = TagLib.File.Create(track.FilePath);
                if (track.Bpm > 0) file.Tag.BeatsPerMinute = (uint)track.Bpm;
                if (!string.IsNullOrWhiteSpace(track.MusicalKey))
                    AdvancedTagIO.WriteCustomField(file, "TKEY", track.MusicalKey);
                file.Save();
                return;
            }
            catch (System.IO.IOException) { System.Threading.Thread.Sleep(TagWriteRetryDelayMs); }
            catch { return; }
        }
    }
}
