using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// Drives the Audio Converter dialog — owns the format/bitrate/output settings,
/// runs the sequential conversion job, and tracks per-file status.
/// </summary>
public partial class AudioConverterViewModel : ViewModelBase
{
    private readonly IAudioConverterService _service;
    private readonly IReadOnlyList<Track> _tracks;
    private CancellationTokenSource? _cts;

    public string TitleText { get; }
    public bool HasFfmpeg => _service.GetFfmpegPath() != null;

    // ── Format selection ──
    public string[] FormatOptions { get; } = { "mp3", "flac", "opus", "aac", "wav" };
    [ObservableProperty] private string _selectedFormat = "mp3";

    public bool BitrateApplies => SelectedFormat is "mp3" or "opus" or "aac";
    partial void OnSelectedFormatChanged(string value) => OnPropertyChanged(nameof(BitrateApplies));

    public int[] BitrateOptions { get; } = { 96, 128, 160, 192, 256, 320 };
    [ObservableProperty] private int _selectedBitrate = 320;

    // ── Output ──
    [ObservableProperty] private string _outputFolder = string.Empty;
    [ObservableProperty] private string _filenamePattern = "%artist% - %title%";
    [ObservableProperty] private bool _copyTags = true;
    [ObservableProperty] private bool _embedArtwork = true;
    [ObservableProperty] private bool _overwriteExisting;

    // ── Job state ──
    public ObservableCollection<JobRow> Jobs { get; } = new();
    [ObservableProperty] private bool _isConverting;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public event EventHandler? Closed;

    public AudioConverterViewModel(IReadOnlyList<Track> tracks, IAudioConverterService service)
    {
        _tracks = tracks;
        _service = service;

        TitleText = $"Convert {tracks.Count} track{(tracks.Count == 1 ? string.Empty : "s")}";
        foreach (var t in tracks)
            Jobs.Add(new JobRow { Track = t, Status = "Pending" });

        if (!HasFfmpeg)
            StatusMessage = "ffmpeg not found — set the path in Settings or install ffmpeg on your PATH.";
    }

    [RelayCommand]
    private async Task Start()
    {
        if (IsConverting || !HasFfmpeg) return;

        IsConverting = true;
        StatusMessage = "Converting…";
        _cts = new CancellationTokenSource();

        var options = new AudioConvertOptions
        {
            Format = SelectedFormat,
            BitrateKbps = SelectedBitrate,
            OutputFolder = OutputFolder,
            FilenamePattern = FilenamePattern,
            CopyTags = CopyTags,
            EmbedArtwork = EmbedArtwork,
            OverwriteExisting = OverwriteExisting,
        };

        var progress = new Progress<ConvertProgress>(p =>
        {
            // Progress callbacks come on a background thread; marshal updates
            // to the UI thread so the JobRow rebind doesn't race the renderer.
            Dispatcher.UIThread.Post(() =>
            {
                var row = Jobs.FirstOrDefault(j => j.Track == p.Track);
                if (row == null) return;
                row.Status = p.Status;
                row.Done = p.Done;
                row.Failed = p.Failed;
                row.OutputPath = p.OutputPath;
            });
        });

        try
        {
            await Task.Run(() => _service.ConvertAsync(_tracks, options, progress, _cts.Token));
            var done = Jobs.Count(j => j.Done && !j.Failed);
            var failed = Jobs.Count(j => j.Failed);
            StatusMessage = $"Finished · Done: {done} · Failed: {failed}";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cancelled.";
        }
        finally
        {
            IsConverting = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsConverting)
        {
            _cts?.Cancel();
            return;
        }
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Per-file row shown in the dialog's progress list.</summary>
    public partial class JobRow : ObservableObject
    {
        public Track Track { get; set; } = null!;
        public string TrackTitle => Track?.Title ?? string.Empty;
        public string TrackSubtitle => Track == null ? string.Empty : ($"{Track.Artist} · {Track.Album}");

        [ObservableProperty] private string _status = string.Empty;
        [ObservableProperty] private bool _done;
        [ObservableProperty] private bool _failed;
        [ObservableProperty] private string _outputPath = string.Empty;
    }
}
