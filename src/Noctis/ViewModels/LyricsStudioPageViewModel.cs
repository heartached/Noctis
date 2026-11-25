using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.LyricsStudio;

namespace Noctis.ViewModels;

/// <summary>
/// The sidebar's Lyrics Studio page. Each visit scans the local library for the songs that
/// lack the format the Studio writes (ELRC with word timings on, any timed lyrics off), shows
/// the per-format counts, and opens a <see cref="LyricsStudioViewModel"/> over the first 40 —
/// unless a run or a review is in progress, in which case the page keeps its work.
/// </summary>
public partial class LyricsStudioPageViewModel : ViewModelBase
{
    /// <summary>First-N cap so a run stays reviewable (same cap as the old Settings button).</summary>
    public const int MaxQueue = 40;

    private readonly ILibraryService _library;
    private readonly Func<bool> _wordTimings;
    private readonly Func<IReadOnlyList<Track>, LyricsStudioViewModel> _createStudio;
    private readonly DetectFormatsFunc _detectFormats;
    private int _generation;

    // The format scan reads every .lrc/.elrc in the library (thousands of files), so its result
    // is kept across visits and only redone when the library changed or the Studio saved lyrics.
    private List<Track>? _scannedLocal;
    private IReadOnlyList<LyricsFormat>? _scannedFormats;
    private bool _scanDirty = true;
    // Bumped with every _scanDirty: a scan started at an older version is stale, a running one
    // at the current version is joined (a second visit mid-scan read every sidecar again).
    private int _libraryVersion;
    private FormatScan? _scan;

    [ObservableProperty] private LyricsStudioViewModel? _studio;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusText = string.Empty;
    /// <summary>ELRC / LRC / plain / none counts over the local library (empty until the scan finishes).</summary>
    [ObservableProperty] private IReadOnlyList<LyricsStudioCount> _counts = Array.Empty<LyricsStudioCount>();

    /// <summary>
    /// Whether the two backdrop cards (lyrics background video, music videos) are folded out.
    /// They are settings, not work, so the page opens straight onto the queue and review and
    /// keeps them one click away behind the header's Backdrops pill.
    /// </summary>
    [ObservableProperty] private bool _showBackdrops;

    /// <summary>The library-wide format pass: ExistingLyricsLoader.DetectFormats with a per-song report (tests slow it down).</summary>
    public delegate IReadOnlyList<LyricsFormat> DetectFormatsFunc(IReadOnlyList<Track> tracks, CancellationToken ct, Action<int, LyricsFormat>? detected);

    public bool ShowEmpty => !IsLoading && Studio is null;
    public bool HasCounts => Counts.Count > 0;

    public LyricsStudioPageViewModel(ILibraryService library, Func<bool> wordTimings,
        Func<IReadOnlyList<Track>, LyricsStudioViewModel> createStudio, DetectFormatsFunc? detectFormats = null)
    {
        _library = library;
        _wordTimings = wordTimings;
        _createStudio = createStudio;
        _detectFormats = detectFormats ?? ExistingLyricsLoader.DetectFormats;
        _library.LibraryUpdated += (_, _) => MarkScanDirty();
    }

    private void MarkScanDirty()
    {
        _scanDirty = true;
        Interlocked.Increment(ref _libraryVersion);
    }

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowEmpty));
    partial void OnStudioChanged(LyricsStudioViewModel? value) => OnPropertyChanged(nameof(ShowEmpty));
    partial void OnCountsChanged(IReadOnlyList<LyricsStudioCount> value) => OnPropertyChanged(nameof(HasCounts));

    /// <summary>True when the open Studio must not be replaced by a rescan: a run, a review on screen,
    /// or lyrics typed or transcribed into a song's box (a rescan after a save used to drop them).</summary>
    public bool IsBusy => Studio is { IsRunning: true } or { HasReview: true } or { HasUnsavedLyricsBox: true };

    /// <summary>The queue was hand-picked (Choose songs): a visit keeps it until every pick is saved, skipped or failed.</summary>
    private bool _customQueue;

    /// <summary>
    /// Choose songs (user ask 09-19): run the Studio over exactly these songs, writing the
    /// chosen format. A Studio mid-run or mid-review keeps its work and takes the picks on
    /// the end of its queue; otherwise the page's auto pick is replaced.
    /// </summary>
    public void UsePicked(IReadOnlyList<Track> tracks, bool wordTimings)
    {
        var local = tracks.Where(t => t.SourceType == SourceType.Local)
            .GroupBy(t => t.Id).Select(g => g.First()).ToList();
        if (local.Count == 0) return;
        if (Studio is { } current && IsBusy)
        {
            current.AddTracks(local);
            current.WordTimings = wordTimings;
        }
        else
        {
            var studio = _createStudio(local);
            studio.WordTimings = wordTimings;
            Studio = studio;
        }
        _customQueue = true;
        StatusText = string.Empty;
    }

    /// <summary>
    /// Choose songs, nothing typed: the songs that lack the chosen format, from the page's
    /// cached format scan (a cold or stale cache is scanned first, off the UI thread, and
    /// the counts refreshed with it).
    /// </summary>
    public async Task<IReadOnlyList<Track>> SuggestMissingAsync(bool wordTimings, int max)
    {
        var local = _library.Tracks.Where(t => t.SourceType == SourceType.Local).ToList();
        if (_scanDirty || _scannedLocal is null || _scannedFormats is null || _scannedLocal.Count != local.Count)
            return await PickFirstMissingAsync(StartOrJoinScan(local), wordTimings, max);
        return PickMissing(_scannedLocal, _scannedFormats, wordTimings, max);
    }

    private bool KeepsCustomQueue()
    {
        if (!_customQueue) return false;
        if (Studio is { } s && s.Queue.Any(i => i.Status is LyricsStudioViewModel.StudioStatus.Waiting
                or LyricsStudioViewModel.StudioStatus.Working or LyricsStudioViewModel.StudioStatus.Ready
                or LyricsStudioViewModel.StudioStatus.Loaded))
            return true;
        _customQueue = false;
        return false;
    }

    public async Task RefreshAsync()
    {
        if (IsBusy || KeepsCustomQueue()) return;
        var generation = ++_generation;
        var wordTimings = _wordTimings();
        if (Studio is { SavedCount: > 0 }) MarkScanDirty();

        // Cached scan: re-pick without touching disk, and keep the open Studio when it already
        // holds exactly that queue (selection and drafts survive the round trip).
        if (!_scanDirty && _scannedLocal is { } cachedLocal && _scannedFormats is { } cachedFormats
            && cachedLocal.Count == _library.Tracks.Count(t => t.SourceType == SourceType.Local))
        {
            var repick = PickMissing(cachedLocal, cachedFormats, wordTimings, MaxQueue);
            if (!SameQueue(Studio, repick))
                Studio = repick.Count > 0 ? _createStudio(repick) : null;
            StatusText = repick.Count == 0 ? "Every local song already has the chosen format." : string.Empty;
            return;
        }

        IsLoading = true;
        StatusText = Localization.Loc.T("LyricsStudio.Loading");
        try
        {
            var local = _library.Tracks.Where(t => t.SourceType == SourceType.Local).ToList();
            // Format detection reads disk per track (off the UI thread). On a hard drive a cold pass
            // took 35-55 s for 7,212 songs, so the queue opens as soon as the scan has met its songs;
            // the counts, which need every song, follow when the scan finishes.
            var scan = StartOrJoinScan(local);
            var picked = await PickFirstMissingAsync(scan, wordTimings, MaxQueue);
            if (generation != _generation) return;
            Studio = picked.Count > 0 ? _createStudio(picked) : null;
            StatusText = picked.Count == 0
                ? "Every local song already has the chosen format."
                : string.Empty;
            IsLoading = false;

            await (_scan ?? scan).Committed;
        }
        catch (Exception ex)
        {
            if (generation != _generation) return;
            Studio = null;
            StatusText = $"Could not scan the library — {ex.Message}";
        }
        finally
        {
            if (generation == _generation) IsLoading = false;
        }
    }

    /// <summary>The running scan when it is still current for this library, else a new one (the stale one is cancelled).</summary>
    private FormatScan StartOrJoinScan(List<Track> local)
    {
        var version = Volatile.Read(ref _libraryVersion);
        if (_scan is { } running && !running.Full.IsCompleted && running.Version == version && running.Local.Count == local.Count)
            return running;
        _scan?.Cancel();
        var scan = new FormatScan(local, version, _detectFormats);
        _scan = scan;
        scan.Committed = CommitWhenDoneAsync(scan);
        return scan;
    }

    /// <summary>A finished scan becomes the page's cached scan and its counts (unless a newer scan replaced it).</summary>
    private async Task CommitWhenDoneAsync(FormatScan scan)
    {
        IReadOnlyList<LyricsFormat> formats;
        try { formats = await scan.Full; }
        catch { return; } // cancelled by a newer scan, or failed: the caller awaiting the pick reports it
        if (_scan != scan) return;
        _scannedLocal = scan.Local;
        _scannedFormats = formats;
        if (scan.Version == Volatile.Read(ref _libraryVersion)) _scanDirty = false;
        Counts = SettingsViewModel.BuildLyricsStudioCounts(formats);
    }

    /// <summary>
    /// <see cref="PickMissing"/> as soon as the scan has met <paramref name="max"/> songs lacking the
    /// format: the same songs a pick from the finished scan gives, since both take the first ones in
    /// library order. A scan cancelled for a newer one (the library changed) hands over to that one.
    /// </summary>
    private async Task<List<Track>> PickFirstMissingAsync(FormatScan scan, bool wordTimings, int max)
    {
        while (true)
        {
            try
            {
                var early = scan.PrefixWithMissing(wordTimings, max);
                var formats = await Task.WhenAny(early, scan.Full) == early ? await early : await scan.Full;
                return PickMissing(scan.Local, formats, wordTimings, max);
            }
            catch (OperationCanceledException) when (_scan is { } newer && newer != scan)
            {
                scan = newer;
            }
        }
    }

    /// <summary>
    /// One library-wide format scan on the thread pool. It reports each song's format as it goes,
    /// so <see cref="PrefixWithMissing"/> can answer the queue before the last song is read.
    /// </summary>
    private sealed class FormatScan
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly object _gate = new();
        private readonly LyricsFormat[] _formats;
        private readonly List<(bool WordTimings, int Max, TaskCompletionSource<IReadOnlyList<LyricsFormat>> Done)> _waiters = new();
        private int _scanned, _missingWords, _missingLines;

        public List<Track> Local { get; }
        public int Version { get; }
        public Task<IReadOnlyList<LyricsFormat>> Full { get; }
        public Task Committed { get; set; } = Task.CompletedTask;

        public FormatScan(List<Track> local, int version, DetectFormatsFunc detect)
        {
            Local = local;
            Version = version;
            _formats = new LyricsFormat[local.Count];
            Full = Task.Run(() => Run(detect));
        }

        public void Cancel() => _cts.Cancel();

        private IReadOnlyList<LyricsFormat> Run(DetectFormatsFunc detect)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                var formats = detect(Local, _cts.Token, Detected);
                DebugLog.Write("Lyrics", $"Lyrics Studio: format scan of {Local.Count} song(s) took {watch.ElapsedMilliseconds} ms");
                return formats;
            }
            catch (OperationCanceledException)
            {
                DebugLog.Write("Lyrics", $"Lyrics Studio: format scan stopped for a newer one after {_scanned} of {Local.Count} song(s), {watch.ElapsedMilliseconds} ms");
                throw;
            }
            catch (Exception ex)
            {
                DebugLog.Write("Lyrics", $"Lyrics Studio: format scan failed after {_scanned} of {Local.Count} song(s): {ex.Message}");
                throw;
            }
        }

        private void Detected(int index, LyricsFormat format)
        {
            lock (_gate)
            {
                _formats[index] = format;
                _scanned = index + 1;
                if (!LyricsFormatDetector.AlreadyHas(format, wordTimings: true)) _missingWords++;
                if (!LyricsFormatDetector.AlreadyHas(format, wordTimings: false)) _missingLines++;
                for (var i = _waiters.Count - 1; i >= 0; i--)
                {
                    if (Missing(_waiters[i].WordTimings) < _waiters[i].Max) continue;
                    _waiters[i].Done.TrySetResult(_formats[.._scanned]);
                    _waiters.RemoveAt(i);
                }
            }
        }

        /// <summary>The formats scanned so far once they hold <paramref name="max"/> songs lacking the format;
        /// never completes when the library holds fewer (await <see cref="Full"/> beside it).</summary>
        public Task<IReadOnlyList<LyricsFormat>> PrefixWithMissing(bool wordTimings, int max)
        {
            lock (_gate)
            {
                if (max > 0 && Missing(wordTimings) >= max)
                    return Task.FromResult<IReadOnlyList<LyricsFormat>>(_formats[.._scanned]);
                var done = new TaskCompletionSource<IReadOnlyList<LyricsFormat>>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (max > 0) _waiters.Add((wordTimings, max, done));
                return done.Task;
            }
        }

        private int Missing(bool wordTimings) => wordTimings ? _missingWords : _missingLines;
    }

    private static bool SameQueue(LyricsStudioViewModel? studio, List<Track> picked)
    {
        if (studio is null) return picked.Count == 0;
        if (studio.Queue.Count != picked.Count) return false;
        for (var i = 0; i < picked.Count; i++)
            if (studio.Queue[i].Track.Id != picked[i].Id) return false;
        return true;
    }

    /// <summary>The first <paramref name="max"/> tracks whose lyrics lack the chosen format.</summary>
    internal static List<Track> PickMissing(IReadOnlyList<Track> tracks, IReadOnlyList<LyricsFormat> formats, bool wordTimings, int max)
    {
        var picked = new List<Track>(Math.Min(max, tracks.Count));
        for (var i = 0; i < tracks.Count && i < formats.Count && picked.Count < max; i++)
            if (!LyricsFormatDetector.AlreadyHas(formats[i], wordTimings)) picked.Add(tracks[i]);
        return picked;
    }
}
