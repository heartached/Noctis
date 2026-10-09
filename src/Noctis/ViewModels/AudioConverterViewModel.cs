using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// Drives the Audio Converter dialog — owns the format/bitrate/output settings,
/// runs the sequential conversion job, and tracks per-file status.
/// </summary>
public partial class AudioConverterViewModel : ViewModelBase
{
    private static string L(string key) => Localization.Loc.T(key);
    private static string L(string key, params object[] args) => Localization.Loc.T(key, args);

    private readonly IAudioConverterService _service;
    private readonly ILibraryService _library;
    private readonly IReadOnlyList<Track> _tracks;
    private readonly bool _hasFfmpeg;
    private CancellationTokenSource? _cts;

    /// <summary>Track -> row for progress updates (one lookup per report, not a list scan).</summary>
    private readonly Dictionary<Track, JobRow> _rowsByTrack = new();

    public string TitleText { get; }

    /// <summary>Resolved once: GetFfmpegPath probes the disk, and CanStart is queried often.</summary>
    public bool HasFfmpeg => _hasFfmpeg;

    // ── Format selection ──
    // Sourced from the service's descriptor table so the list (and its lossy/lossless
    // classification) stays in one place.
    public string[] FormatOptions { get; } =
        AudioConverterService.OutputFormats.Select(f => f.Key).ToArray();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BitrateApplies), nameof(BitDepthApplies), nameof(PcmBitDepthApplies),
        nameof(ArtworkApplies), nameof(EmbedArtworkEffective), nameof(ExampleName))]
    private string _selectedFormat = "mp3";

    private OutputFormatInfo? Format => AudioConverterService.FindFormat(SelectedFormat);

    /// <summary>Bitrate applies to lossy formats only; bit depth to lossless only.</summary>
    public bool BitrateApplies => Format is { IsLossless: false };
    public bool BitDepthApplies => Format is { IsLossless: true } && !IsPcm;

    /// <summary>
    /// WAV and AIFF: the encoder has no "keep the source depth" mode, "Auto" was written as
    /// 16-bit (pcm_s16le / pcm_s16be), so a 24-bit source silently lost its depth under a
    /// label that promised otherwise. These formats get their own 16 / 24 choice.
    /// </summary>
    public bool PcmBitDepthApplies => IsPcm;
    private bool IsPcm => SelectedFormat is "wav" or "aiff";

    /// <summary>Only some containers carry an attached cover; the service drops it for the rest.</summary>
    public bool ArtworkApplies => Format is { ArtworkSupported: true };

    public int[] BitrateOptions { get; } = { 96, 128, 160, 192, 256, 320 };
    [ObservableProperty] private int _selectedBitrate = 320;

    // ── Bit depth (lossless formats only) ──
    public string[] BitDepthOptions { get; } = { "Auto", "16", "24" };
    [ObservableProperty] private string _selectedBitDepth = "Auto";

    public string[] PcmBitDepthOptions { get; } = { "16", "24" };
    [ObservableProperty] private string _selectedPcmBitDepth = "16";

    // ── Output ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderError), nameof(HasFolderError))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _outputFolder = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PatternError), nameof(HasPatternError), nameof(ExampleName))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _filenamePattern = "%artist% - %title%";

    [ObservableProperty] private bool _copyTags = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmbedArtworkEffective))]
    private bool _embedArtwork = true;

    /// <summary>What the Embed artwork box shows: off for a format that can't hold a cover
    /// (the preference itself is kept for the next format that can).</summary>
    public bool EmbedArtworkEffective
    {
        get => EmbedArtwork && ArtworkApplies;
        set { if (ArtworkApplies) EmbedArtwork = value; }
    }

    [ObservableProperty] private bool _overwriteExisting;

    /// <summary>When set, converted files are imported into the library (shown in the app)
    /// with a " (FORMAT)" title suffix; otherwise they are only written to disk.</summary>
    [ObservableProperty] private bool _addToLibrary = true;

    /// <summary>The tag chips under the filename pattern (the view inserts the token at the caret).</summary>
    public IReadOnlyList<PatternToken> Tokens { get; } = new PatternToken[]
    {
        new("%artist%", "AudioConverter.TokenArtist"),
        new("%albumartist%", "AudioConverter.TokenAlbumArtist"),
        new("%album%", "AudioConverter.TokenAlbum"),
        new("%title%", "AudioConverter.TokenTitle"),
        new("%tracknumber2%", "AudioConverter.TokenTrack"),
        new("%discnumber%", "AudioConverter.TokenDisc"),
        new("%year%", "AudioConverter.TokenYear"),
        new("%genre%", "AudioConverter.TokenGenre"),
        new("%composer%", "AudioConverter.TokenComposer"),
    };

    public sealed record PatternToken(string Token, string LabelKey)
    {
        public string Label => Localization.Loc.T(LabelKey);
    }

    // ── Validation ──

    /// <summary>Characters a file name can't hold. The pattern's literal text is not
    /// sanitized (only tag values are), so "%artist%: %title%" reached ffmpeg as
    /// "Artist: Title.mp3" — on NTFS that writes an alternate data stream of a file named
    /// "Artist" instead of the song.</summary>
    private static readonly char[] InvalidNameChars = { ':', '*', '?', '"', '<', '>', '|' };

    public string PatternError =>
        FilenamePattern?.IndexOfAny(InvalidNameChars) >= 0 ? L("AudioConverter.PatternInvalid") : string.Empty;
    public bool HasPatternError => PatternError.Length > 0;

    /// <summary>A typed folder must be a full path: a relative one ("Music") resolved against
    /// the app's working directory, so the files landed somewhere the user never chose.</summary>
    public string FolderError
    {
        get
        {
            var folder = OutputFolder?.Trim() ?? string.Empty;
            if (folder.Length == 0) return string.Empty;
            if (!Path.IsPathFullyQualified(folder)) return L("AudioConverter.FolderNotFull");
            // Past the drive's "C:" a colon (or any of these) is not a valid path on Windows.
            var tail = OperatingSystem.IsWindows() && folder.Length > 2 ? folder[2..] : folder;
            if (OperatingSystem.IsWindows() && tail.IndexOfAny(InvalidNameChars) >= 0)
                return L("AudioConverter.FolderNotFull");
            return string.Empty;
        }
    }
    public bool HasFolderError => FolderError.Length > 0;

    /// <summary>The output name for the first track, as the service will build it.</summary>
    public string ExampleName
    {
        get
        {
            if (_tracks.Count == 0 || HasPatternError) return string.Empty;
            var t = _tracks[0];
            var pattern = string.IsNullOrWhiteSpace(FilenamePattern) ? "%title%" : FilenamePattern;
            var name = TitleFormatter.Expand(pattern, t, sanitizeForFilename: true);
            if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(t.FilePath);
            return name + (Format?.Extension ?? "." + SelectedFormat);
        }
    }

    // ── Job state ──
    public ObservableCollection<JobRow> Jobs { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(CancelLabel))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _isConverting;

    /// <summary>Settings are captured when a run starts; they're locked while it runs.</summary>
    public bool IsIdle => !IsConverting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CancelLabel))]
    private bool _hasRun;

    /// <summary>Cancel before a run, Stop during one, Close once something ran.</summary>
    public string CancelLabel => IsConverting ? L("AudioConverter.Stop")
        : HasRun ? L("AudioConverter.Close") : L("AudioConverter.Cancel");

    [ObservableProperty] private string _statusMessage = string.Empty;

    public event EventHandler? Closed;

    public AudioConverterViewModel(IReadOnlyList<Track> tracks, IAudioConverterService service, ILibraryService library)
    {
        _service = service;
        _library = library;
        _hasFfmpeg = service.GetFfmpegPath() != null;

        // One row per file. The same track listed twice (a playlist that holds it twice)
        // converted it twice into "Song (2).mp3", and both reports landed on the first row,
        // so the second sat at "Pending" after the run.
        var unique = new List<Track>(tracks.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tracks)
        {
            if (_rowsByTrack.ContainsKey(t) || !seen.Add(t.FilePath ?? string.Empty)) continue;
            var row = new JobRow { Track = t };
            row.Reset();
            unique.Add(t);
            Jobs.Add(row);
            _rowsByTrack[t] = row;
        }
        _tracks = unique;

        TitleText = _tracks.Count == 1
            ? L("AudioConverter.TitleOne")
            : L("AudioConverter.TitleMany", _tracks.Count);

        // A 24-bit source keeps its depth by default when going to WAV/AIFF.
        if (_tracks.Any(t => t.BitsPerSample >= 24))
            _selectedPcmBitDepth = "24";

        if (!HasFfmpeg)
            StatusMessage = L("AudioConverter.NoFfmpeg");
    }

    public bool CanStart => !IsConverting && HasFfmpeg && _tracks.Count > 0 && !HasPatternError && !HasFolderError;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        if (IsConverting) return;

        // Surface why nothing happens instead of silently ignoring the click.
        if (!HasFfmpeg)
        {
            StatusMessage = L("AudioConverter.NoFfmpeg");
            return;
        }
        if (!CanStart) return;

        // A second run (another format, say) starts from clean rows: they used to keep the
        // previous run's Done/Failed until their turn came round.
        foreach (var row in Jobs) row.Reset();

        IsConverting = true;
        StatusMessage = L("AudioConverter.Converting");
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;

        var options = new AudioConvertOptions
        {
            Format = SelectedFormat,
            BitrateKbps = SelectedBitrate,
            BitDepth = IsPcm ? SelectedPcmBitDepth : SelectedBitDepth,
            OutputFolder = OutputFolder?.Trim() ?? string.Empty,
            FilenamePattern = FilenamePattern,
            CopyTags = CopyTags,
            EmbedArtwork = EmbedArtworkEffective,
            OverwriteExisting = OverwriteExisting,
            AppendFormatToTitle = AddToLibrary,
        };

        var progress = new OrderedProgress<ConvertProgress>(p =>
        {
            if (_rowsByTrack.TryGetValue(p.Track, out var row)) row.Apply(p);
        });

        try
        {
            var summary = await Task.Run(() => _service.ConvertAsync(_tracks, options, progress, ct));
            progress.Drain();

            // ffmpeg killed by Cancel is reported as a failed file and the run returns
            // normally when that was the last (or only) file — it read "Finished · 0
            // converted · 1 failed" for a conversion the user stopped.
            ct.ThrowIfCancellationRequested();

            // Surface the converted files in the app when requested.
            if (AddToLibrary && summary.OutputPaths.Count > 0)
                await _library.ImportFilesAsync(summary.OutputPaths, ct);

            StatusMessage = BuildStatus(summary);
        }
        catch (OperationCanceledException)
        {
            progress.Drain();
            foreach (var row in Jobs) row.MarkCancelledIfUnfinished();
            StatusMessage = L("AudioConverter.Cancelled");
        }
        catch (Exception ex)
        {
            // An unwritable output folder (Directory.CreateDirectory) or a failed import
            // escaped the command: the footer kept reading "Converting…" and the row kept
            // its spinner, with no sign anything went wrong.
            progress.Drain();
            DebugLog.Write("AudioConverter", ex);
            foreach (var row in Jobs) row.MarkFailedIfUnfinished(ex.Message);
            StatusMessage = L("AudioConverter.FailedWith", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
            HasRun = true;
            IsConverting = false;
        }
    }

    /// <summary>Leads with the converted count; appends failed/skipped only when nonzero.</summary>
    private static string BuildStatus(ConvertSummary s)
    {
        var status = L("AudioConverter.Finished", s.Converted);
        if (s.Failed > 0) status += " · " + L("AudioConverter.FinishedFailed", s.Failed);
        if (s.Skipped > 0) status += " · " + L("AudioConverter.FinishedSkipped", s.Skipped);
        return status;
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsConverting)
        {
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            return;
        }
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Called by the window's Closing handler. The only way to stop a run was the Cancel
    /// button, so Alt+F4 left ffmpeg converting (and the import running) behind a closed
    /// dialog.
    /// </summary>
    public void CancelForClose()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Progress that keeps reports in order with the run's end. Progress&lt;T&gt; (plus the
    /// extra Dispatcher.Post it used to wrap) delivered reports up to two hops later than
    /// the awaited result, so a late "Failed: cancelled" could land after the dialog had
    /// marked the row Cancelled. Reports queue here, are applied on the UI context, and
    /// <see cref="Drain"/> applies whatever is left before the end state is written.
    /// </summary>
    internal sealed class OrderedProgress<T> : IProgress<T>
    {
        private readonly ConcurrentQueue<T> _pending = new();
        private readonly Action<T> _apply;
        private readonly object _gate = new();
        // The UI context the run started on. Without one (a unit test) reports apply at once.
        private readonly SynchronizationContext? _context = SynchronizationContext.Current;

        public OrderedProgress(Action<T> apply) => _apply = apply;

        public void Report(T value)
        {
            _pending.Enqueue(value);
            if (_context is { } context) context.Post(static s => ((OrderedProgress<T>)s!).Drain(), this);
            else Drain();
        }

        public void Drain()
        {
            lock (_gate)
            {
                while (_pending.TryDequeue(out var item)) _apply(item);
            }
        }
    }

    /// <summary>Per-file row shown in the dialog's progress list.</summary>
    public partial class JobRow : ObservableObject
    {
        public Track Track { get; set; } = null!;
        public string TrackTitle => Track?.Title ?? string.Empty;
        public string TrackSubtitle => Track == null ? string.Empty
            : string.IsNullOrWhiteSpace(Track.Album) ? Track.Artist : $"{Track.Artist} · {Track.Album}";

        /// <summary>The detailed status (the service's words), shown as the chip's tooltip.</summary>
        [ObservableProperty] private string _status = string.Empty;
        [ObservableProperty] private bool _done;
        [ObservableProperty] private bool _failed;
        [ObservableProperty] private string _outputPath = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ChipText), nameof(IsWorking), nameof(IsSucceeded), nameof(IsSkipped),
            nameof(IsFailedState), nameof(IsQuiet))]
        private JobState _state;

        public bool IsWorking => State == JobState.Working;
        public bool IsSucceeded => State == JobState.Done;
        public bool IsSkipped => State == JobState.Skipped;
        public bool IsFailedState => State == JobState.Failed;
        public bool IsQuiet => State is JobState.Pending or JobState.Cancelled;

        public string ChipText => State switch
        {
            JobState.Working => L("AudioConverter.StateConverting"),
            JobState.Done => L("AudioConverter.StateDone"),
            JobState.Skipped => L("AudioConverter.StateSkipped"),
            JobState.Failed => L("AudioConverter.StateFailed"),
            JobState.Cancelled => L("AudioConverter.StateCancelled"),
            _ => L("AudioConverter.StatePending"),
        };

        /// <summary>Chip tooltip: where the file went, or why it didn't.</summary>
        public string Detail => State == JobState.Done && OutputPath.Length > 0 ? OutputPath : Status;
        partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(Detail));
        partial void OnOutputPathChanged(string value) => OnPropertyChanged(nameof(Detail));

        internal void Reset()
        {
            Status = "Pending";
            Done = false;
            Failed = false;
            OutputPath = string.Empty;
            State = JobState.Pending;
        }

        internal void Apply(ConvertProgress p)
        {
            Status = p.Status;
            Done = p.Done;
            Failed = p.Failed;
            OutputPath = p.OutputPath;
            State = !p.Done ? JobState.Working
                : p.Failed ? (p.Status.EndsWith("cancelled", StringComparison.OrdinalIgnoreCase) ? JobState.Cancelled : JobState.Failed)
                : p.Status.StartsWith("Skipped", StringComparison.Ordinal) ? JobState.Skipped
                : JobState.Done;
            OnPropertyChanged(nameof(Detail));
        }

        internal void MarkCancelledIfUnfinished()
        {
            if (State is JobState.Pending or JobState.Working or JobState.Cancelled)
            {
                Status = "Cancelled";
                Failed = false;
                State = JobState.Cancelled;
            }
        }

        internal void MarkFailedIfUnfinished(string error)
        {
            if (State == JobState.Working)
            {
                Status = "Failed: " + error;
                Done = true;
                Failed = true;
                State = JobState.Failed;
            }
        }
    }

    public enum JobState { Pending, Working, Done, Skipped, Failed, Cancelled }
}
