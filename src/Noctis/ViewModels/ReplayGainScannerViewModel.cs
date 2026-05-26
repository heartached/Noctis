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
    }

    [RelayCommand]
    private async Task Start()
    {
        if (IsScanning || !_service.IsAvailable) return;
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
            await Task.Run(() => _service.ScanAsync(_tracks, AlbumMode, progress, _cts.Token));
            var done = Jobs.Count(j => j.Done && !j.Failed);
            var failed = Jobs.Count(j => j.Failed);
            StatusMessage = $"Finished · Scanned: {done} · Failed: {failed}";
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
