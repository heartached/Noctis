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

    public event EventHandler? Closed;

    public ReplayGainScannerViewModel(IReadOnlyList<Track> tracks, IReplayGainScannerService service, ILibraryService library)
    {
        _tracks = tracks;
        _service = service;
        _library = library;

        TitleText = $"Scan ReplayGain · {tracks.Count} track{(tracks.Count == 1 ? string.Empty : "s")}";
        foreach (var t in tracks)
            Jobs.Add(new RgJobRow { Track = t, Status = "Pending" });

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
                var row = Jobs.FirstOrDefault(j => j.Track == t);
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
                var row = Jobs.FirstOrDefault(j => j.Track == p.Track);
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
