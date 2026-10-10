using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// "Send to Folder" (MusicBee's Send To → Folder (Copy)): copy a selection to a drive or
/// folder, flat or organised with the user's file pattern, identical files skipped.
/// </summary>
public partial class SendToFolderViewModel : ViewModelBase
{
    private readonly IReadOnlyList<Track> _tracks;
    private readonly ISendToFolderService _service;
    private readonly string _organizePattern;
    private CancellationTokenSource? _cts;
    private IReadOnlyList<SendToFolderItem> _plan = Array.Empty<SendToFolderItem>();
    // The folder the current plan was built for (Show in folder opens it).
    private string _planRoot = string.Empty;

    private static string L(string key) => Localization.Loc.T(key);
    private static string L(string key, params object[] args) => Localization.Loc.T(key, args);

    public string TitleText { get; }

    [ObservableProperty] private string _destination = string.Empty;
    [ObservableProperty] private bool _organizeIntoFolders;
    [ObservableProperty] private bool _includeLyrics = true;

    /// <summary>GitHub #121 (2026-10-10): move instead of copy (off by default). The library
    /// follows each moved song; its lyrics files always go with it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartLabel), nameof(CanChooseLyrics))]
    private bool _moveFiles;

    /// <summary>The primary button: Copy, or Move.</summary>
    public string StartLabel => MoveFiles ? L("SendTo.Move") : L("SendTo.Copy");

    /// <summary>Lyrics are a choice for a copy; a move always takes them along.</summary>
    public bool CanChooseLyrics => !MoveFiles;

    // The mode the current plan was built for (a move plan never skips on a size match).
    private bool _planMove;

    /// <summary>Points the live playlists and the play log at moved songs' new ids (tests swap it).</summary>
    internal Func<IReadOnlyDictionary<Guid, Guid>, Task> ApplyTrackIdRemap { get; set; } = static remap =>
    {
        if (remap.Count == 0) return Task.CompletedTask;
        App.Services?.GetService<IPlayHistoryService>()?.RemapTrackIds(remap);
        return App.Services?.GetService<MainWindowViewModel>()?.Sidebar.ApplyTrackIdRemapAsync(remap)
               ?? Task.CompletedTask;
    };

    /// <summary>The user's Organize Files pattern (the example's tooltip).</summary>
    public string OrganizePatternText => _organizePattern;

    /// <summary>The example's tooltip: the pattern the folders follow.</summary>
    public string ExampleTip => L("SendToFolder.ExampleTip", _organizePattern);

    /// <summary>Where the first song lands, one chip per folder and the file name last
    /// ("Bad Bunny" › "Un Verano Sin Ti" › "03 Tití Me Preguntó.flac"), following the
    /// Organize toggle.</summary>
    public ObservableCollection<PathChip> ExampleChips { get; } = new();

    public ObservableCollection<PlanRow> Rows { get; } = new();
    [ObservableProperty] private string _planSummary = string.Empty;
    [ObservableProperty] private bool _hasPlan;

    /// <summary>Why there is no plan for the typed destination (missing / relative folder).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDestinationError))]
    private string _destinationError = string.Empty;
    public bool HasDestinationError => DestinationError.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(CancelLabel))]
    private bool _isCopying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CancelLabel))]
    private bool _hasRun;

    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>Errors of the last run, one line each (shown under the list).</summary>
    public ObservableCollection<string> Errors { get; } = new();
    [ObservableProperty] private bool _hasErrors;

    /// <summary>Options are taken when a run starts; they're locked while it runs.</summary>
    public bool IsIdle => !IsCopying;

    public bool HasRows => Rows.Count > 0;

    /// <summary>Cancel before a run, Stop during one, Close once something ran (the converter's labels).</summary>
    public string CancelLabel => IsCopying ? L("SendToFolder.Stop")
        : HasRun ? L("SendTo.Close") : L("SendToFolder.Cancel");

    public bool CanStart => HasPlan && !IsCopying && !IsDone;

    /// <summary>Show in folder: after a run, when the songs are there (copied now or before).</summary>
    public bool CanShowInFolder => IsDone && !IsCopying && _landedTargets.Count > 0;

    private readonly List<string> _landedTargets = new();
    private bool _closing;

    /// <summary>Opens the destination (tests swap it; default is the platform file manager).
    /// Arguments: the file to select, or the folder to open when files went to several.</summary>
    internal Action<string, bool> Reveal { get; set; } = static (path, isFile) =>
    {
        if (isFile) PlatformHelper.ShowInFileManager(path);
        else PlatformHelper.OpenFolder(path);
    };

    public event EventHandler? Closed;

    public SendToFolderViewModel(IReadOnlyList<Track> tracks, ISendToFolderService service, string organizePattern, string? initialDestination = null)
    {
        _tracks = tracks;
        _service = service;
        _organizePattern = string.IsNullOrWhiteSpace(organizePattern) ? FileOrganizePlanner.DefaultPattern : organizePattern;
        // Counted as the plan counts: the same file twice (a playlist holding it twice) is one
        // copy, and the title said "2 songs" over a single row.
        var count = tracks.Where(t => t is not null && !string.IsNullOrWhiteSpace(t.FilePath))
            .Select(t => t.FilePath).Distinct(PathComparison.Comparer).Count();
        TitleText = count == 1 ? L("SendToFolder.TitleOne") : L("SendToFolder.TitleMany", count);
        Rows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRows));
        UpdateExample();
        if (!string.IsNullOrWhiteSpace(initialDestination)) Destination = initialDestination;
        else StatusMessage = PlanSummary = L("SendToFolder.PickFolder");
    }

    // The Destination box pushes every keystroke here, and a plan stats each track's source,
    // target and lyrics on the destination drive (often a slow USB stick): wait for the typing
    // to pause, and build the plan off the UI thread.
    private const int DestinationDebounceMs = 300;
    private CancellationTokenSource? _rebuildCts;

    /// <summary>The last plan rebuild (tests await it).</summary>
    internal Task PlanRebuild { get; private set; } = Task.CompletedTask;

    partial void OnDestinationChanged(string value) => RebuildPlan(DestinationDebounceMs);
    partial void OnOrganizeIntoFoldersChanged(bool value)
    {
        UpdateExample();
        RebuildPlan(0);
    }
    partial void OnIncludeLyricsChanged(bool value) => RebuildPlan(0);
    partial void OnMoveFilesChanged(bool value)
    {
        // Left behind, a moved song's lyrics would detach from it (paired by basename).
        if (value && !IncludeLyrics) IncludeLyrics = true;
        RebuildPlan(0);
    }
    partial void OnHasPlanChanged(bool value) => NotifyStartState();
    partial void OnIsCopyingChanged(bool value) => NotifyStartState();
    partial void OnIsDoneChanged(bool value) => NotifyStartState();

    private void NotifyStartState()
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanShowInFolder));
        StartCommand.NotifyCanExecuteChanged();
        ShowInFolderCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The example chips: the first song through the pattern (or its own name when flat).</summary>
    private void UpdateExample()
    {
        ExampleChips.Clear();
        var track = _tracks.FirstOrDefault(t => t is not null && !string.IsNullOrWhiteSpace(t.FilePath));
        if (track is null) return;
        try
        {
            // Planned against a stand-in root: only the part under it is shown.
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "noctis-example"));
            var item = SendToFolderPlanner.Plan(new[] { track }, root, OrganizeIntoFolders ? _organizePattern : null,
                false, _ => null).FirstOrDefault();
            if (item is null) return;
            var parts = Path.GetRelativePath(root, item.TargetPath).Split(Path.DirectorySeparatorChar);
            for (var i = 0; i < parts.Length; i++)
                ExampleChips.Add(new PathChip(parts[i], IsFile: i == parts.Length - 1, IsFirst: i == 0));
        }
        catch (Exception)
        {
            // An odd file path makes no example; the option still works.
        }
    }

    /// <param name="keepRunState">After a stopped run: re-plan (what went over now reads
    /// "Already there", so Copy carries on where it stopped) but keep the run's status and errors.</param>
    private void RebuildPlan(int delayMs, bool keepRunState = false)
    {
        if (IsCopying) return;
        IsDone = false;
        _rebuildCts?.Cancel();
        _rebuildCts?.Dispose();
        var cts = new CancellationTokenSource();
        _rebuildCts = cts;
        // The old plan no longer matches the options: Copy waits for the new one.
        _plan = Array.Empty<SendToFolderItem>();
        HasPlan = false;
        PlanRebuild = RebuildPlanAsync(delayMs, keepRunState, cts.Token);
    }

    private async Task RebuildPlanAsync(int delayMs, bool keepRunState, CancellationToken token)
    {
        IReadOnlyList<SendToFolderItem>? plan = null;
        var root = string.Empty;
        var move = false;
        string? error = null;
        try
        {
            if (delayMs > 0) await Task.Delay(delayMs, token);
            if (token.IsCancellationRequested) return;
            root = Destination?.Trim() ?? string.Empty;
            var pattern = OrganizeIntoFolders ? _organizePattern : null;
            var includeLyrics = IncludeLyrics;
            move = MoveFiles;
            if (root.Length == 0) { }
            // "Music" or "." resolved against the app's working directory: the songs went
            // into the program folder instead of anywhere the user picked.
            else if (!IsFullyQualified(root)) error = L("SendToFolder.NeedFullPath");
            else
            {
                plan = await Task.Run(
                    () => Directory.Exists(root)
                        ? move ? _service.Plan(_tracks, root, pattern, includeLyrics, move: true)
                               : _service.Plan(_tracks, root, pattern, includeLyrics)
                        : null, token);
                if (plan is null) error = L("SendToFolder.FolderMissing");
            }
            if (token.IsCancellationRequested || IsCopying) return;
        }
        catch (OperationCanceledException) { return; /* superseded by a newer change */ }
        catch (Exception ex)
        {
            // The planner stats the destination drive; a drive that vanished mid-plan must not
            // escape the fire-and-forget rebuild.
            DebugLog.Write("SendToFolder", $"Plan failed: {ex.Message}");
            plan = null;
            error = L("SendToFolder.FolderMissing");
        }

        Rows.Clear();
        _landedTargets.Clear();
        if (!keepRunState)
        {
            Errors.Clear();
            HasErrors = false;
            Progress = 0;
        }
        DestinationError = error ?? string.Empty;
        if (plan is null)
        {
            _plan = Array.Empty<SendToFolderItem>();
            _planRoot = string.Empty;
            HasPlan = false;
            PlanSummary = root.Length == 0 ? L("SendToFolder.PickFolder") : string.Empty;
            if (!keepRunState) StatusMessage = PlanSummary;
            return;
        }
        _plan = plan;
        _planMove = move;
        _planRoot = Path.GetFullPath(root);
        foreach (var item in _plan)
            Rows.Add(new PlanRow(item, _planRoot, move));
        var copy = _plan.Count(p => p.Action != SendToFolderAction.SkipIdentical);
        var skip = _plan.Count - copy;
        var lyricsOnly = _plan.Count(p => p.Action == SendToFolderAction.SkipIdentical && p.Sidecars.Count > 0);
        var lyrics = _plan.Sum(p => p.Sidecars.Count);
        var parts = new List<string>();
        if (copy > 0) parts.Add(L(move ? "SendToFolder.SummaryMove" : "SendToFolder.SummaryCopy", copy));
        if (skip > 0) parts.Add(L("SendToFolder.SummaryThere", skip));
        if (lyrics > 0) parts.Add(L("SendToFolder.SummaryLyrics", lyrics));
        PlanSummary = copy == 0 && lyricsOnly == 0 ? L("SendToFolder.AllThere") : string.Join(" · ", parts);
        if (!keepRunState) StatusMessage = PlanSummary;
        // An already-copied song still runs when it is missing lyrics.
        HasPlan = copy > 0 || lyricsOnly > 0;
    }

    private static bool IsFullyQualified(string path)
    {
        try { return Path.IsPathFullyQualified(path); }
        catch (Exception) { return false; }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        if (!CanStart) return;
        IsCopying = true;
        HasRun = true;
        Progress = 0;
        Errors.Clear();
        HasErrors = false;
        _landedTargets.Clear();
        foreach (var row in Rows) row.Reset();
        var plan = _plan;
        var move = _planMove;
        var progressKey = move ? "SendToFolder.ProgressMove" : "SendToFolder.Progress";
        var total = plan.Count;
        StatusMessage = L(progressKey, 0, total);
        var cts = _cts = new CancellationTokenSource();
        var stopped = false;

        // Reports applied in order and drained before the end state is written. The old
        // Progress + Dispatcher.Post pair landed a report two hops late, after the result:
        // the footer read "Finishing…" over "Done" (the converter's fix, same class).
        var progress = new AudioConverterViewModel.OrderedProgress<SendToFolderProgress>(p =>
        {
            if (!IsCopying) return;
            Progress = p.Total == 0 ? 1 : p.Done / (double)p.Total;
            StatusMessage = L(progressKey, p.Done, p.Total);
            if (p.Index >= 0 && p.Index < Rows.Count) Rows[p.Index].Apply(p.Outcome, p.Error);
            if ((p.Outcome is SendToFolderOutcome.Copied or SendToFolderOutcome.Skipped) && p.Index >= 0 && p.Index < plan.Count)
                _landedTargets.Add(plan[p.Index].TargetPath);
        });

        try
        {
            var result = move
                ? await _service.MoveAsync(plan, progress, cts.Token)
                : await _service.CopyAsync(plan, progress, cts.Token);
            progress.Drain();
            // Moved songs have new ids: playlists and the play log follow them (Organize Files' step).
            if (move && result.TrackIdRemap.Count > 0)
            {
                try { await ApplyTrackIdRemap(result.TrackIdRemap); }
                catch (Exception ex) { DebugLog.Write("SendToFolder", $"Remap after move failed: {ex.Message}"); }
            }
            foreach (var row in Rows) row.FinishUnfinished(result.Cancelled);
            foreach (var error in result.Errors) Errors.Add(error);
            HasErrors = Errors.Count > 0;
            StatusMessage = BuildStatus(result, move);
            IsDone = !result.Cancelled;
            stopped = result.Cancelled;
        }
        catch (Exception ex)
        {
            progress.Drain();
            DebugLog.Write("SendToFolder", ex);
            foreach (var row in Rows) row.FinishUnfinished(cancelled: false, ex.Message);
            Errors.Add(ex.Message);
            HasErrors = true;
            StatusMessage = L("SendToFolder.FailedWith", ex.Message);
            stopped = true; // what went over before the throw is there now: re-plan
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
            IsCopying = false;
            Progress = 1;
            NotifyStartState();
        }

        // A stopped (or broken) run's plan is stale: the songs that went over now sit at their
        // targets, so running it again failed each of them with "file already exists".
        // Re-plan; unless the window is closing (CancelForClose), with nothing to re-plan for.
        if (stopped && !_closing) RebuildPlan(0, keepRunState: true);
    }

    private static string BuildStatus(SendToFolderResult r, bool move)
    {
        var status = move
            ? r.Cancelled ? L("SendToFolder.StoppedMove", r.Copied) : L("SendToFolder.DoneMove", r.Copied)
            : r.Cancelled ? L("SendToFolder.Stopped", r.Copied) : L("SendToFolder.Done", r.Copied);
        if (r.Skipped > 0) status += " · " + L("SendToFolder.SummaryThere", r.Skipped);
        if (r.Failed > 0) status += " · " + L("SendToFolder.DoneFailed", r.Failed);
        return status;
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsCopying)
        {
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            return;
        }
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The window is closing by any route (Esc, Close, Alt+F4, the owner going away):
    /// stop a run; the service removes the half-written file it was on.</summary>
    public void CancelForClose()
    {
        _closing = true;
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        try { _rebuildCts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    [RelayCommand(CanExecute = nameof(CanShowInFolder))]
    private void ShowInFolder()
    {
        if (!CanShowInFolder) return;
        // Everything in one folder (flat, or one album): select the first file there.
        // Spread over artist/album folders: open the destination itself.
        var dirs = _landedTargets.Select(p => Path.GetDirectoryName(p) ?? string.Empty).Distinct(PathComparison.Comparer).Count();
        if (dirs == 1) Reveal(_landedTargets[0], true);
        else Reveal(_planRoot, false);
    }

    /// <summary>One folder or the file name in the example path.</summary>
    public sealed record PathChip(string Text, bool IsFile, bool IsFirst);

    public enum RowState { Pending, Working, Copied, Skipped, Failed, Stopped }

    public sealed partial class PlanRow : ObservableObject
    {
        private readonly string _pendingText;
        // A move's chips read Move / Moving / Moved (GitHub #121).
        private readonly bool _move;

        public PlanRow(SendToFolderItem item, string root, bool move = false)
        {
            _move = move;
            Title = string.IsNullOrWhiteSpace(item.Track.Title) ? Path.GetFileNameWithoutExtension(item.SourcePath) : item.Track.Title;
            Subtitle = item.Track.ArtistDisplay;
            var rel = Path.GetRelativePath(root, item.TargetPath);
            Target = rel.StartsWith("..", StringComparison.Ordinal) ? item.TargetPath : rel;
            IsSkip = item.Action == SendToFolderAction.SkipIdentical;
            HasLyrics = item.Sidecars.Count > 0;
            _pendingText = item.Action switch
            {
                SendToFolderAction.SkipIdentical when HasLyrics => L("SendToFolder.StateAddLyrics"),
                SendToFolderAction.SkipIdentical => L("SendToFolder.StateThere"),
                SendToFolderAction.Renamed => L("SendToFolder.StateRenamed"),
                _ => L(move ? "SendToFolder.StateMove" : "SendToFolder.StateCopy"),
            };
            Detail = item.Action == SendToFolderAction.Renamed ? L("SendToFolder.RenamedTip") : item.TargetPath;
            ChipText = _pendingText;
        }

        public string Title { get; }
        public string Subtitle { get; }
        public string Target { get; }
        public bool IsSkip { get; }
        public bool HasLyrics { get; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsWorking), nameof(IsCopied), nameof(IsSkipped), nameof(IsFailed), nameof(Done))]
        private RowState _state;

        [ObservableProperty] private string _chipText = string.Empty;
        [ObservableProperty] private string _detail = string.Empty;

        public bool IsWorking => State == RowState.Working;
        public bool IsCopied => State == RowState.Copied;
        public bool IsSkipped => State == RowState.Skipped;
        public bool IsFailed => State == RowState.Failed;
        /// <summary>Finished one way or another.</summary>
        public bool Done => State is RowState.Copied or RowState.Skipped or RowState.Failed;

        internal void Reset()
        {
            State = RowState.Pending;
            ChipText = _pendingText;
        }

        internal void Apply(SendToFolderOutcome outcome, string? error)
        {
            switch (outcome)
            {
                case SendToFolderOutcome.Working:
                    State = RowState.Working;
                    ChipText = L(_move ? "SendToFolder.StateMoving" : "SendToFolder.StateCopying");
                    break;
                case SendToFolderOutcome.Copied:
                    State = RowState.Copied;
                    ChipText = L(_move ? "SendToFolder.StateMoved" : "SendToFolder.StateCopied");
                    break;
                case SendToFolderOutcome.Skipped:
                    State = RowState.Skipped;
                    ChipText = L("SendToFolder.StateThere");
                    break;
                case SendToFolderOutcome.Failed:
                    State = RowState.Failed;
                    ChipText = L("SendToFolder.StateFailed");
                    if (!string.IsNullOrEmpty(error)) Detail = error;
                    break;
            }
        }

        /// <summary>The run ended: a row still pending or working was stopped (or failed with the run).</summary>
        internal void FinishUnfinished(bool cancelled, string? error = null)
        {
            if (State is not (RowState.Pending or RowState.Working)) return;
            if (cancelled)
            {
                // Pending rows keep their plan chip; only the one in flight reads Stopped.
                if (State == RowState.Working)
                {
                    State = RowState.Stopped;
                    ChipText = L("SendToFolder.StateStopped");
                }
                return;
            }
            if (error is not null) Apply(SendToFolderOutcome.Failed, error);
        }
    }
}
