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
    private readonly IReadOnlyList<Track> _tracks;
    private CancellationTokenSource? _cts;
    // Cancels the background "already scanned" tag reads so they never hold a file
    // handle while a scan writes to the same file (or after the dialog is closed).
    private readonly CancellationTokenSource _initCts = new();

    public string TitleText { get; }
    public bool IsServiceAvailable => _service.IsAvailable;

    [ObservableProperty] private bool _albumMode = true;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public ObservableCollection<RgJobRow> Jobs { get; } = new();

    /// <summary>
    /// Track -> row index for progress updates. The progress callback did
    /// `Jobs.FirstOrDefault(j => j.Track == ...)` twice per track, which is O(n) each —
    /// O(n²) across a scan, on the UI thread, for a selection that can be thousands of
    /// tracks (Ctrl+A in the Songs view feeds straight through).
    /// </summary>
    private readonly Dictionary<Track, RgJobRow> _rowsByTrack = new();

    public event EventHandler? Closed;

    public ReplayGainScannerViewModel(IReadOnlyList<Track> tracks, IReplayGainScannerService service, ILibraryService library)
    {
        _tracks = tracks;
        _service = service;
        _library = library;

        TitleText = $"Scan ReplayGain · {tracks.Count} track{(tracks.Count == 1 ? string.Empty : "s")}";
        foreach (var t in tracks)
        {
            var row = new RgJobRow { Track = t, Status = "Pending" };
            Jobs.Add(row);
            _rowsByTrack[t] = row;
        }

        if (!_service.IsAvailable)
            StatusMessage = "ffmpeg not found — set the path in Settings → Audio Tools.";

        // Flag tracks that already carry ReplayGain tags so the user can tell a
        // re-scan from a first scan. Reading tags is file IO, so do it off the UI thread.
        _ = MarkAlreadyScannedAsync();
    }

    /// <summary>Reads each track's tags and labels rows that already have a
    /// REPLAYGAIN_TRACK_GAIN value as "Already scanned" (re-scanning still works).</summary>
    private async Task MarkAlreadyScannedAsync()
    {
        // Wrapped whole. This is started fire-and-forget, and Start() cancels _initCts —
        // which made the pending `await Task.Run(..., ct)` throw TaskCanceledException out
        // of here with nobody observing it, so pressing Start (a completely normal action)
        // surfaced a logged error via TaskScheduler.UnobservedTaskException.
        try
        {
            await MarkAlreadyScannedCoreAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* superseded by Start() or dialog close */ }
        catch (Exception ex)
        {
            DebugLog.Write("ReplayGain", $"Pre-scan tag read failed: {ex.Message}");
        }
    }

    private async Task MarkAlreadyScannedCoreAsync()
    {
        var ct = _initCts.Token;
        foreach (var t in _tracks)
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
        if (IsScanning || !_service.IsAvailable) return;
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
            var summary = await Task.Run(() => _service.ScanAsync(_tracks, AlbumMode, progress, _cts.Token));
            StatusMessage = $"Finished · {summary.Scanned} scanned"
                + (summary.Failed > 0 ? $" · {summary.Failed} failed" : string.Empty);
            // Refresh the library so any in-app view (e.g. metadata window) that
            // reads RG tags picks up the new values.
            _library.NotifyMetadataChanged();
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
