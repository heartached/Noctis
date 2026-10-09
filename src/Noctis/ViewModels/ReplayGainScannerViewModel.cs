using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

public partial class ReplayGainScannerViewModel : ViewModelBase
{
    private static string L(string key) => Localization.Loc.T(key);
    private static string L(string key, params object[] args) => Localization.Loc.T(key, args);

    private readonly IReplayGainScannerService _service;
    private readonly ILibraryService _library;
    private readonly List<Track> _tracks = new();
    // Resolves a file picked with "Add files…" to a Track (the library entry when the
    // path is indexed, else a tag read). Null hides the button.
    private readonly Func<string, Track?>? _resolveFile;
    private readonly bool _isAvailable;
    private CancellationTokenSource? _cts;
    // Cancels the background "already scanned" tag reads so they never hold a file
    // handle while a scan writes to the same file (or after the dialog is closed).
    private readonly CancellationTokenSource _initCts = new();

    [ObservableProperty] private string _titleText = string.Empty;
    /// <summary>Resolved once: IsAvailable probes the disk, and CanStart is queried often.</summary>
    public bool IsServiceAvailable => _isAvailable;
    public bool CanAddFiles => _resolveFile != null;

    [ObservableProperty] private bool _albumMode = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditSelection), nameof(CanAddMore), nameof(CancelLabel))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _isScanning;

    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>
    /// True once a scan ran to completion. The primary button then reads "Done" and
    /// closes the dialog instead of silently rescanning the whole selection; Cancel
    /// is hidden because there is nothing left to cancel.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryButtonText), nameof(ShowCancel), nameof(CanEditSelection), nameof(CanAddMore))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _hasFinished;

    public string PrimaryButtonText => HasFinished ? L("ReplayGainScanner.Done") : L("ReplayGainScanner.Scan");
    public bool ShowCancel => !HasFinished;
    public string CancelLabel => IsScanning ? L("ReplayGainScanner.Stop") : L("ReplayGainScanner.Cancel");

    /// <summary>
    /// Rows can be ticked/unticked only before a scan. The boxes stayed live during one:
    /// unticking a queued row changed nothing (the scan had its list) but the title's count
    /// dropped, so it read "1 track" while two were being tagged.
    /// </summary>
    public bool CanEditSelection => !IsScanning && !HasFinished;

    /// <summary>"Add files…" was still clickable after a finished scan, where AddFilesAsync
    /// ignores it — a button that did nothing.</summary>
    public bool CanAddMore => CanAddFiles && !IsScanning && !HasFinished;

    public ObservableCollection<RgJobRow> Jobs { get; } = new();

    /// <summary>
    /// Track -> row index for progress updates. The progress callback did
    /// `Jobs.FirstOrDefault(j => j.Track == ...)` twice per track, which is O(n) each —
    /// O(n²) across a scan, on the UI thread, for a selection that can be thousands of
    /// tracks (Ctrl+A in the Songs view feeds straight through).
    /// </summary>
    private readonly Dictionary<Track, RgJobRow> _rowsByTrack = new();
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? Closed;

    /// <summary>Raised when a scan finishes, so the player can re-read the new tags.</summary>
    public event EventHandler? ScanCompleted;

    public ReplayGainScannerViewModel(IReadOnlyList<Track> tracks, IReplayGainScannerService service, ILibraryService library,
        Func<string, Track?>? resolveFile = null)
    {
        _service = service;
        _library = library;
        _resolveFile = resolveFile;
        _isAvailable = service.IsAvailable;

        AddRows(tracks);

        if (!_isAvailable)
            StatusMessage = L("ReplayGainScanner.NoFfmpeg");

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
            if (_rowsByTrack.ContainsKey(t) || !_paths.Add(t.FilePath ?? string.Empty))
                continue;
            var row = new RgJobRow { Track = t, Status = "Pending" };
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(RgJobRow.IsIncluded)) return;
                UpdateTitle();
                // A row ticked back in after a cancelled scan read "Skipped" from that run.
                if (row.IsIncluded && row.State == RgState.Skipped) row.ResetIdle();
                StartCommand.NotifyCanExecuteChanged();
            };
            _tracks.Add(t);
            Jobs.Add(row);
            _rowsByTrack[t] = row;
            added.Add(t);
        }
        UpdateTitle();
        StartCommand.NotifyCanExecuteChanged();
        return added;
    }

    private void UpdateTitle()
    {
        var count = Jobs.Count(j => j.IsIncluded);
        TitleText = count == 1
            ? L("ReplayGainScanner.TitleOne")
            : L("ReplayGainScanner.TitleMany", count);
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
        // The scan may have started while the files were read.
        if (IsScanning || HasFinished) return;
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
        catch (ObjectDisposedException) { /* dialog closed */ }
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
                if (!_rowsByTrack.TryGetValue(t, out var row)) return;
                row.HadTags = true;
                // Only relabel the idle "Pending" state — never overwrite an
                // in-progress or finished scan from this session.
                if (row.State == RgState.Pending) row.ResetIdle();
            });
        }
    }

    public bool CanStart => HasFinished || (!IsScanning && _isAvailable && Jobs.Any(j => j.IsIncluded));

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        if (HasFinished)
        {
            // "Done": the scan already ran, just dismiss.
            _initCts.Cancel();
            Closed?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (IsScanning || !_isAvailable) return;
        // Only the ticked rows are measured and tagged (GitHub #105).
        var selected = Jobs.Where(j => j.IsIncluded).Select(j => j.Track).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = L("ReplayGainScanner.TickOne");
            return;
        }
        foreach (var row in Jobs)
        {
            if (row.IsIncluded) row.ResetForRun();
            else row.MarkSkipped();
        }
        // Stop the pre-scan tag reads so they can't hold a handle while we write.
        try { _initCts.Cancel(); } catch (ObjectDisposedException) { }
        var albumMode = AlbumMode;
        IsScanning = true;
        StatusMessage = L("ReplayGainScanner.Scanning");
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;

        RgJobRow? measuring = null;
        var progress = new AudioConverterViewModel.OrderedProgress<ScanProgress>(p =>
        {
            if (!_rowsByTrack.TryGetValue(p.Track, out var row)) return;
            // Album mode measures every track before it writes any: each row read
            // "Measuring…" (with its spinner) from its turn until the whole selection had
            // been measured. The previous one is measured once the next one starts.
            if (!p.Done && measuring != null && !ReferenceEquals(measuring, row) && measuring.State == RgState.Working)
                measuring.MarkMeasured();
            row.Apply(p, albumMode);
            measuring = p.Done ? null : row;
        });

        try
        {
            var summary = await Task.Run(() => _service.ScanAsync(selected, albumMode, progress, ct));
            progress.Drain();
            StatusMessage = L("ReplayGainScanner.Finished", summary.Scanned)
                + (summary.Failed > 0 ? " · " + L("ReplayGainScanner.FinishedFailed", summary.Failed) : string.Empty);
            // Refresh the library so any in-app view (e.g. metadata window) that
            // reads RG tags picks up the new values.
            _library.NotifyMetadataChanged();
            ScanCompleted?.Invoke(this, EventArgs.Empty);
            HasFinished = true;
        }
        catch (OperationCanceledException)
        {
            // The service rethrows the cancel without a last report, so the row being
            // measured kept "Measuring…" and its spinner after the scan had stopped.
            progress.Drain();
            foreach (var row in Jobs) row.MarkCancelledIfUnfinished();
            StatusMessage = L("ReplayGainScanner.Cancelled");
        }
        catch (Exception ex)
        {
            // Anything else escaped the command and left "Scanning…" in the footer.
            progress.Drain();
            DebugLog.Write("ReplayGain", ex);
            foreach (var row in Jobs) row.MarkCancelledIfUnfinished();
            StatusMessage = L("ReplayGainScanner.FailedWith", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
            IsScanning = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsScanning) { try { _cts?.Cancel(); } catch (ObjectDisposedException) { } return; }
        try { _initCts.Cancel(); } catch (ObjectDisposedException) { } // stop any background tag reads
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Called by the window's Closing handler so a scan can't outlive the dialog.
    /// The run's own token source is disposed by the run when it unwinds.
    /// </summary>
    public void CancelForClose()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        try { _initCts.Cancel(); } catch (ObjectDisposedException) { }
    }

    public partial class RgJobRow : ObservableObject
    {
        public Track Track { get; set; } = null!;
        public string TrackTitle => Track?.Title ?? string.Empty;
        public string TrackSubtitle => Track == null ? string.Empty
            : string.IsNullOrWhiteSpace(Track.Album) ? Track.Artist : $"{Track.Artist} · {Track.Album}";
        /// <summary>Ticked rows are scanned; unticked ones are left untouched.</summary>
        [ObservableProperty] private bool _isIncluded = true;
        /// <summary>The service's words for the row's state (the chip's tooltip).</summary>
        [ObservableProperty] private string _status = string.Empty;
        [ObservableProperty] private bool _done;
        [ObservableProperty] private bool _failed;
        [ObservableProperty] private double _trackGainDb;
        [ObservableProperty] private double _albumGainDb;
        /// <summary>The scan that tagged this row wrote an album gain (album mode on).</summary>
        [ObservableProperty] private bool _hasAlbumGain;
        /// <summary>The file carried ReplayGain tags when the dialog opened.</summary>
        public bool HadTags { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ChipText), nameof(IsWorking), nameof(IsSucceeded), nameof(IsSkipped),
            nameof(IsFailedState), nameof(IsTagged), nameof(ShowChip), nameof(ShowGain))]
        private RgState _state;

        public bool IsWorking => State is RgState.Working or RgState.Measured;
        public bool IsSucceeded => State == RgState.Done;
        public bool IsSkipped => State == RgState.Skipped;
        public bool IsFailedState => State == RgState.Failed;
        public bool IsTagged => State == RgState.Tagged;
        /// <summary>A tagged row shows its gain in place of a "Done" chip.</summary>
        public bool ShowChip => !ShowGain;
        public bool ShowGain => State == RgState.Done;

        public string ChipText => State switch
        {
            RgState.Working => L("ReplayGainScanner.StateMeasuring"),
            RgState.Measured => L("ReplayGainScanner.StateMeasured"),
            RgState.Done => L("ReplayGainScanner.StateDone"),
            RgState.Skipped => L("ReplayGainScanner.StateSkipped"),
            RgState.Failed => L("ReplayGainScanner.StateFailed"),
            RgState.Cancelled => L("ReplayGainScanner.StateCancelled"),
            RgState.Tagged => L("ReplayGainScanner.StateTagged"),
            _ => L("ReplayGainScanner.StatePending"),
        };

        /// <summary>The track gain, as the row shows it once tagged.</summary>
        public string TrackGainText => ShowGain ? $"{TrackGainDb:+0.00;-0.00;0.00} dB" : string.Empty;

        /// <summary>Track and album gain. The album part only when an album gain was written:
        /// with album gain off it read "A: +0.00 dB", a value that was never computed.</summary>
        public string GainsText =>
            (Done && !Failed)
                ? (HasAlbumGain
                    ? $"T: {TrackGainDb:+0.00;-0.00;0.00} dB  ·  A: {AlbumGainDb:+0.00;-0.00;0.00} dB"
                    : $"T: {TrackGainDb:+0.00;-0.00;0.00} dB")
                : string.Empty;

        /// <summary>Chip/gain tooltip: the gains when tagged, else the detailed status.</summary>
        public string Detail => GainsText.Length > 0 ? GainsText : Status;

        partial void OnTrackGainDbChanged(double value) => NotifyGains();
        partial void OnAlbumGainDbChanged(double value) => NotifyGains();
        partial void OnHasAlbumGainChanged(bool value) => NotifyGains();
        partial void OnDoneChanged(bool value) => NotifyGains();
        partial void OnFailedChanged(bool value) => NotifyGains();
        partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(Detail));
        partial void OnStateChanged(RgState value) => OnPropertyChanged(nameof(TrackGainText));

        private void NotifyGains()
        {
            OnPropertyChanged(nameof(GainsText));
            OnPropertyChanged(nameof(TrackGainText));
            OnPropertyChanged(nameof(Detail));
        }

        /// <summary>Back to the idle label: "Already scanned" when the file had tags.</summary>
        internal void ResetIdle()
        {
            Status = HadTags ? "Already scanned" : "Pending";
            State = HadTags ? RgState.Tagged : RgState.Pending;
        }

        internal void ResetForRun()
        {
            Done = false;
            Failed = false;
            HasAlbumGain = false;
            ResetIdle();
        }

        internal void MarkSkipped()
        {
            if (Done && !Failed) return; // tagged by an earlier (cancelled) run of this dialog
            Status = "Skipped";
            State = RgState.Skipped;
        }

        internal void MarkMeasured()
        {
            Status = "Measured";
            State = RgState.Measured;
        }

        internal void Apply(ScanProgress p, bool albumMode)
        {
            Status = p.Status;
            Done = p.Done;
            Failed = p.Failed;
            if (p.Done && !p.Failed)
            {
                TrackGainDb = p.TrackGainDb;
                AlbumGainDb = p.AlbumGainDb;
                HasAlbumGain = albumMode;
            }
            State = !p.Done ? RgState.Working : p.Failed ? RgState.Failed : RgState.Done;
        }

        internal void MarkCancelledIfUnfinished()
        {
            if (State is RgState.Pending or RgState.Tagged or RgState.Working or RgState.Measured)
            {
                Status = "Cancelled";
                State = RgState.Cancelled;
            }
        }
    }

    public enum RgState { Pending, Tagged, Working, Measured, Done, Skipped, Failed, Cancelled }
}
