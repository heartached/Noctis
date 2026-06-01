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
    private readonly ILibraryService _library;
    private readonly IReadOnlyList<Track> _tracks;
    private CancellationTokenSource? _cts;

    public string TitleText { get; }
    public bool HasFfmpeg => _service.GetFfmpegPath() != null;

    // ── Format selection ──
    // Sourced from the service's descriptor table so the list (and its lossy/lossless
    // classification) stays in one place.
    public string[] FormatOptions { get; } =
        AudioConverterService.OutputFormats.Select(f => f.Key).ToArray();
    [ObservableProperty] private string _selectedFormat = "mp3";

    /// <summary>Bitrate applies to lossy formats only; bit depth to lossless only.</summary>
    public bool BitrateApplies => AudioConverterService.FindFormat(SelectedFormat) is { IsLossless: false };
    public bool BitDepthApplies => AudioConverterService.FindFormat(SelectedFormat) is { IsLossless: true };

    partial void OnSelectedFormatChanged(string value)
    {
        OnPropertyChanged(nameof(BitrateApplies));
        OnPropertyChanged(nameof(BitDepthApplies));
    }

    public int[] BitrateOptions { get; } = { 96, 128, 160, 192, 256, 320 };
    [ObservableProperty] private int _selectedBitrate = 320;

    // ── Bit depth (lossless formats only) ──
    public string[] BitDepthOptions { get; } = { "Auto", "16", "24" };
    [ObservableProperty] private string _selectedBitDepth = "Auto";

    // ── Output ──
    [ObservableProperty] private string _outputFolder = string.Empty;
    [ObservableProperty] private string _filenamePattern = "%artist% - %title%";
    [ObservableProperty] private bool _copyTags = true;
    [ObservableProperty] private bool _embedArtwork = true;
    [ObservableProperty] private bool _overwriteExisting;

    /// <summary>When set, converted files are imported into the library (shown in the app)
    /// with a " (FORMAT)" title suffix; otherwise they are only written to disk.</summary>
    [ObservableProperty] private bool _addToLibrary = true;

    // ── Job state ──
    public ObservableCollection<JobRow> Jobs { get; } = new();
    [ObservableProperty] private bool _isConverting;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public event EventHandler? Closed;

    public AudioConverterViewModel(IReadOnlyList<Track> tracks, IAudioConverterService service, ILibraryService library)
    {
        _tracks = tracks;
        _service = service;
        _library = library;

        TitleText = $"Convert {tracks.Count} track{(tracks.Count == 1 ? string.Empty : "s")}";
        foreach (var t in tracks)
            Jobs.Add(new JobRow { Track = t, Status = "Pending" });

        if (!HasFfmpeg)
            StatusMessage = "ffmpeg not found. Set its path in Settings.";
    }

    [RelayCommand]
    private async Task Start()
    {
        if (IsConverting) return;

        // Surface why nothing happens instead of silently ignoring the click.
        if (!HasFfmpeg)
        {
            StatusMessage = "Can't start — ffmpeg not found. Set its path in Settings.";
            return;
        }

        IsConverting = true;
        StatusMessage = "Converting…";
        _cts = new CancellationTokenSource();

        var options = new AudioConvertOptions
        {
            Format = SelectedFormat,
            BitrateKbps = SelectedBitrate,
            BitDepth = SelectedBitDepth,
            OutputFolder = OutputFolder,
            FilenamePattern = FilenamePattern,
            CopyTags = CopyTags,
            EmbedArtwork = EmbedArtwork,
            OverwriteExisting = OverwriteExisting,
            AppendFormatToTitle = AddToLibrary,
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
            var summary = await Task.Run(() => _service.ConvertAsync(_tracks, options, progress, _cts.Token));

            // Surface the converted files in the app when requested.
            if (AddToLibrary && summary.OutputPaths.Count > 0)
                await _library.ImportFilesAsync(summary.OutputPaths, _cts.Token);

            StatusMessage = BuildStatus(summary);
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

    /// <summary>Leads with the converted count; appends failed/skipped only when nonzero.</summary>
    private static string BuildStatus(ConvertSummary s)
    {
        var status = $"Finished · {s.Converted} converted";
        if (s.Failed > 0) status += $" · {s.Failed} failed";
        if (s.Skipped > 0) status += $" · {s.Skipped} skipped";
        return status;
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
