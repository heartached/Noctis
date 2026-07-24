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

    // Minimum detector confidence before a value is applied to the track (and possibly
    // written into the user's file). Below these thresholds the result is still cached,
    // so the file is not re-analyzed, but it is treated as "not detected" rather than
    // stamping e.g. "C major @ 128 BPM" onto an ambient or spoken-word recording.
    private const double MinBpmConfidence = 0.30;
    private const double MinKeyConfidence = 0.15;

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

    /// <summary>
    /// Cancels the backfill and waits for it to unwind, bounded by <paramref name="timeout"/>.
    /// Shutdown must use this: Stop() alone only signalled the token, and TryWriteTags is a
    /// synchronous TagLib rewrite that observes no token — so the process could exit midway
    /// through rewriting a large file and leave the user with a truncated track.
    /// </summary>
    public async Task StopAsync(TimeSpan timeout)
    {
        Stop();
        var run = _run;
        if (run is null) return;
        try { await run.WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException) { /* give up waiting; the tag write guards on the token */ }
        catch { /* the run task's own failures are already handled inside RunAsync */ }
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

        DebugLogger.Info(DebugLogger.Category.Playback, "Backfill.Start", $"pending={pending.Count}");

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
                var didDecode = false;
                if (cached != null && cached.FileSize == info.Length && cached.LastModifiedUtc == sig)
                    result = new AudioAnalysisResult(cached.Bpm, cached.BpmConfidence, cached.MusicalKey, cached.KeyConfidence);
                else
                {
                    didDecode = true;
                    result = await _analysis.AnalyzeAsync(track.FilePath, ct);

                    // Record failures too. Skipping the upsert left NeedsAnalysis true
                    // forever, so every DRM'd / corrupt / unsupported file was re-decoded
                    // (a fresh ffmpeg process, up to the full decode timeout) on every
                    // launch and every library update, indefinitely. A zero-result record
                    // against the same size+mtime signature means "already attempted".
                    await _store.UpsertAsync(new TrackAnalysisRecord(
                        track.FilePath, info.Length, sig,
                        result.Failed ? 0 : result.Bpm,
                        result.Failed ? 0 : result.BpmConfidence,
                        result.Failed ? string.Empty : result.MusicalKey,
                        result.Failed ? 0 : result.KeyConfidence,
                        DateTime.UtcNow.ToString("O")), ct);

                    if (result.Failed)
                    {
                        await Task.Delay(PerFileThrottleMs, ct);
                        continue;
                    }
                }

                // Confidence was computed and stored but never consulted, so a detector
                // guess on ambient/spoken/noise material was written to the library — and,
                // with tag writing on, into the user's file — as fact. A bad BPM also
                // drives AutoMixKeyTempo into picking a beat-matched transition for tracks
                // that do not beat-match. Cache the low-confidence result (so it isn't
                // recomputed) but don't apply it.
                bool changed = false;
                if (track.Bpm <= 0 && result.Bpm > 0 && result.BpmConfidence >= MinBpmConfidence)
                { track.Bpm = result.Bpm; changed = true; }
                if (string.IsNullOrWhiteSpace(track.MusicalKey) && !string.IsNullOrWhiteSpace(result.MusicalKey)
                    && result.KeyConfidence >= MinKeyConfidence)
                { track.MusicalKey = result.MusicalKey; changed = true; }

                if (changed)
                {
                    anyWritten = true;
                    if (_settings().WriteAnalysisToTags && !ct.IsCancellationRequested)
                    {
                        if (TryWriteTags(track))
                        {
                            // Re-stamp the cache with the file's post-write size/mtime.
                            // The record above was written before the tag write, so the
                            // signature check on the next pass always missed and forced a
                            // full re-decode — one half of the rewrite loop.
                            try
                            {
                                var after = new System.IO.FileInfo(track.FilePath);
                                await _store.UpsertAsync(new TrackAnalysisRecord(
                                    track.FilePath, after.Length, after.LastWriteTimeUtc.ToString("O"),
                                    result.Bpm, result.BpmConfidence, result.MusicalKey,
                                    result.KeyConfidence, DateTime.UtcNow.ToString("O")), ct);
                            }
                            catch { /* cache refresh is best effort */ }
                        }
                    }
                }

                // Throttle: yield CPU between files so playback/UI stay responsive.
                // Only after real work — applying it to cache hits cost 150ms per already
                // known track on every pass.
                if (didDecode)
                    await Task.Delay(PerFileThrottleMs, ct);
            }
            catch (OperationCanceledException) { break; }
            catch { /* per-file failure: continue */ }
        }

        if (anyWritten)
        {
            try { await _library.SaveAsync(); } catch { }
            // SaveAsync does not raise LibraryUpdated, so nothing told the views that
            // Bpm/MusicalKey had been filled in — the Songs BPM column stayed blank
            // until an unrelated library change or a restart, and the feature read as
            // broken. NotifyMetadataChanged also refreshes the SQLite index.
            try { _library.NotifyMetadataChanged(); } catch { }
        }
    }

    private List<Track> SnapshotPending() =>
        _library.Tracks
            .Where(t => t.SourceType == SourceType.Local && NeedsAnalysis(t))
            .ToList();

    /// <summary>
    /// Writes the detected tempo/key into the file's tags. Returns true when the file
    /// was actually rewritten.
    /// </summary>
    private static bool TryWriteTags(Track track)
    {
        for (int attempt = 0; attempt < TagWriteMaxAttempts; attempt++)
        {
            try
            {
                using var file = TagLib.File.Create(track.FilePath);
                if (track.Bpm > 0) file.Tag.BeatsPerMinute = (uint)track.Bpm;
                if (!string.IsNullOrWhiteSpace(track.MusicalKey))
                {
                    // "INITIALKEY", not "TKEY". WriteCustomField emits a TXXX frame whose
                    // *description* is the key argument — and MetadataService.ReadMusicalKey
                    // only accepts descriptions INITIALKEY / KEY / MUSICALKEY (the bare
                    // "TKEY" it does read is the standard text frame, which this never
                    // wrote). So the value could not be read back by Noctis or by
                    // foobar/Serato/Mixed In Key, the re-import dropped it, and the
                    // backfill re-analyzed and rewrote the file forever.
                    AdvancedTagIO.WriteCustomField(file, "INITIALKEY", track.MusicalKey);
                }
                file.Save();
                return true;
            }
            catch (System.IO.IOException) { System.Threading.Thread.Sleep(TagWriteRetryDelayMs); }
            catch { return false; }
        }
        return false;
    }
}
