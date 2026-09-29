using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

public partial class ReplayGainScannerViewModel : ViewModelBase
{
    private readonly IReplayGainScannerService _service;
    private readonly ILibraryService _library;
    private readonly List<Track> _tracks = new();
    // Resolves a file picked with "Add files…" to a Track (the library entry when the
    // path is indexed, else a tag read). Null hides the button.
    private readonly Func<string, Track?>? _resolveFile;
    private CancellationTokenSource? _cts;
    // Cancels the background "already scanned" tag reads so they never hold a file
    // handle while a scan writes to the same file (or after the dialog is closed).
    private readonly CancellationTokenSource _initCts = new();

    [ObservableProperty] private string _titleText = string.Empty;
    public bool IsServiceAvailable => _service.IsAvailable;
    public bool CanAddFiles => _resolveFile != null;

    [ObservableProperty] private bool _albumMode = true;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>
    /// True once a scan ran to completion. The primary button then reads "Done" and
    /// closes the dialog instead of silently rescanning the whole selection; Cancel
    /// is hidden because there is nothing left to cancel.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryButtonText))]
    [NotifyPropertyChangedFor(nameof(ShowCancel))]
    private bool _hasFinished;

    public string PrimaryButtonText => HasFinished ? "Done" : "Scan";
    public bool ShowCancel => !HasFinished;

    public ObservableCollection<RgJobRow> Jobs { get; } = new();

    /// <summary>
    /// Track -> row index for progress updates. The progress callback did
    /// `Jobs.FirstOrDefault(j => j.Track == ...)` twice per track, which is O(n) each —
    /// O(n²) across a scan, on the UI thread, for a selection that can be thousands of
    /// tracks (Ctrl+A in the Songs view feeds straight through).
    /// </summary>
    private readonly Dictionary<Track, RgJobRow> _rowsByTrack = new();

    public event EventHandler? Closed;

    /// <summary>Raised when a scan finishes, so the player can re-read the new tags.</summary>
    public event EventHandler? ScanCompleted;

    public ReplayGainScannerViewModel(IReadOnlyList<Track> tracks, IReplayGainScannerService service, ILibraryService library,
        Func<string, Track?>? resolveFile = null)
    {
        _service = service;
        _library = library;
        _resolveFile = resolveFile;

        AddRows(tracks);

        if (!_service.IsAvailable)
            StatusMessage = "ffmpeg not found — set the path in Settings → Advanced → Helper programs.";

        // Flag tracks that already carry ReplayGain tags so the user can tell a
        // re-scan from a first scan. Reading tags is file IO, so do it off the UI thread.
        _ = MarkAlreadyScannedAsync();
    }

    /// <summary>
    /// Adds a row per track, skipping any file already listed (the queue can hold the
    /// same Track twice, and a picked file may already be in the list).
    /// </summary>
    private List<Track> AddRows(IEnumerable<Track> tracks)
    {
        var added = new List<Track>();
        foreach (var t in tracks)
        {
            if (_rowsByTrack.ContainsKey(t)
                || _tracks.Any(x => string.Equals(x.FilePath, t.FilePath, StringComparison.OrdinalIgnoreCase)))
                continue;
            var row = new RgJobRow { Track = t, Status = "Pending" };
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(RgJobRow.IsIncluded)) UpdateTitle();
            };
            _tracks.Add(t);
            Jobs.Add(row);
            _rowsByTrack[t] = row;
            added.Add(t);
        }
        UpdateTitle();
        return added;
    }

    private void UpdateTitle()
    {
        var count = Jobs.Count(j => j.IsIncluded);
        TitleText = $"Scan ReplayGain · {count} track{(count == 1 ? string.Empty : "s")}";
    }

    /// <summary>
    /// "Add files…" (GitHub #105): any audio file, in the library or not, joins the list.
    /// Tag reads run off the UI thread.
    /// </summary>
    public async Task AddFilesAsync(IReadOnlyList<string> paths)
    {
        if (_resolveFile is not { } resolve || IsScanning || HasFinished || paths.Count == 0) return;
        var resolved = await Task.Run(() => paths
            .Select(p => { try { return resolve(p); } catch { return null; } })
            .OfType<Track>()
            .ToList());
        var added = AddRows(resolved);
        if (added.Count > 0)
            _ = MarkAlreadyScannedAsync(added);
    }

    /// <summary>Reads each track's tags and labels rows that already have a
    /// REPLAYGAIN_TRACK_GAIN value as "Already scanned" (re-scanning still works).</summary>
    private async Task MarkAlreadyScannedAsync(IReadOnlyList<Track>? tracks = null)
    {
        // Wrapped whole. This is started fire-and-forget, and Start() cancels _initCts —
        // which made the pending `await Task.Run(..., ct)` throw TaskCanceledException out
        // of here with nobody observing it, so pressing Start (a completely normal action)
        // surfaced a logged error via TaskScheduler.UnobservedTaskException.
        try
        {
            await MarkAlreadyScannedCoreAsync(tracks ?? _tracks.ToList()).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* superseded by Start() or dialog close */ }
        catch (Exception ex)
        {
            DebugLog.Write("ReplayGain", $"Pre-scan tag read failed: {ex.Message}");
        }
    }

    private async Task MarkAlreadyScannedCoreAsync(IReadOnlyList<Track> tracks)
    {
        var ct = _initCts.Token;
        foreach (var t in tracks)
        {
            if (ct.IsCancellationRequested) return;

            bool scanned = false;
            await Task.Run(() =>
            {
                try { scanned = !string.IsNullOrWhiteSpace(AdvancedTagIO.ReadAll(t.FilePath).ReplayGainTrackGain); }
                catch { /* unreadable file — treat as not scanned */ }
            }, ct).ConfigureAwait(false);

            if (!scanned || ct.IsCancellationRequested) continue;
            Dispatcher.UIThread.Post(() =>
            {
                _rowsByTrack.TryGetValue(t, out var row);
                // Only relabel the idle "Pending" state — never overwrite an
                // in-progress or finished scan from this session.
                if (row is { Done: false, Status: "Pending" })
                    row.Status = "Already scanned";
            });
        }
    }

    [RelayCommand]
    private async Task Start()
    {
        if (HasFinished)
        {
            // "Done": the scan already ran, just dismiss.
            _initCts.Cancel();
            Closed?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (IsScanning || !_service.IsAvailable) return;
        // Only the ticked rows are measured and tagged (GitHub #105).
        var selected = Jobs.Where(j => j.IsIncluded).Select(j => j.Track).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "Tick at least one track to scan.";
            return;
        }
        foreach (var row in Jobs.Where(j => !j.IsIncluded))
            row.Status = "Skipped";
        // Stop the pre-scan tag reads so they can't hold a handle while we write.
        _initCts.Cancel();
        IsScanning = true;
        StatusMessage = "Scanning…";
        _cts = new CancellationTokenSource();

        var progress = new Progress<ScanProgress>(p =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                _rowsByTrack.TryGetValue(p.Track, out var row);
                if (row == null) return;
                row.Status = p.Status;
                row.Done = p.Done;
                row.Failed = p.Failed;
                if (p.Done && !p.Failed)
                {
                    row.TrackGainDb = p.TrackGainDb;
                    row.AlbumGainDb = p.AlbumGainDb;
                }
            });
        });

        try
        {
            var summary = await Task.Run(() => _service.ScanAsync(selected, AlbumMode, progress, _cts.Token));
            StatusMessage = $"Finished · {summary.Scanned} scanned"
                + (summary.Failed > 0 ? $" · {summary.Failed} failed" : string.Empty);
            // Refresh the library so any in-app view (e.g. metadata window) that
            // reads RG tags picks up the new values.
            _library.NotifyMetadataChanged();
            ScanCompleted?.Invoke(this, EventArgs.Empty);
            HasFinished = true;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cancelled.";
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsScanning) { _cts?.Cancel(); return; }
        _initCts.Cancel(); // stop any background tag reads before the dialog closes
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Called by the window's Closing handler so a scan can't outlive the dialog.
    /// Cancels both token sources and disposes them (neither was ever disposed).
    /// </summary>
    public void CancelForClose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _initCts.Cancel(); } catch { }
        try { _cts?.Dispose(); } catch { }
        try { _initCts.Dispose(); } catch { }
        _cts = null;
    }

    public partial class RgJobRow : ObservableObject
    {
        public Track Track { get; set; } = null!;
        public string TrackTitle => Track?.Title ?? string.Empty;
        public string TrackSubtitle => Track == null ? string.Empty : ($"{Track.Artist} · {Track.Album}");
        /// <summary>Ticked rows are scanned; unticked ones are left untouched.</summary>
        [ObservableProperty] private bool _isIncluded = true;
        [ObservableProperty] private string _status = string.Empty;
        [ObservableProperty] private bool _done;
        [ObservableProperty] private bool _failed;
        [ObservableProperty] private double _trackGainDb;
        [ObservableProperty] private double _albumGainDb;
        public string GainsText =>
            (Done && !Failed)
                ? $"T: {TrackGainDb:+0.00;-0.00;0.00} dB  ·  A: {AlbumGainDb:+0.00;-0.00;0.00} dB"
                : string.Empty;
        partial void OnTrackGainDbChanged(double value) => OnPropertyChanged(nameof(GainsText));
        partial void OnAlbumGainDbChanged(double value) => OnPropertyChanged(nameof(GainsText));
        partial void OnDoneChanged(bool value) => OnPropertyChanged(nameof(GainsText));
    }
}
