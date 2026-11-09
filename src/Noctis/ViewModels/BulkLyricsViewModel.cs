using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services.Lyrics;

namespace Noctis.ViewModels;

/// <summary>
/// Bulk lyrics over a selection or the whole library: look every song up in the lyrics sources
/// switched on in Settings and save the richest find (word-synced first), optionally upgrading
/// plain or line-synced lyrics; or remove what the app wrote. A run keeps going in the
/// background when the dialog is hidden, and a stopped run resumes where it left off.
/// </summary>
public partial class BulkLyricsViewModel : ViewModelBase
{
    private readonly IReadOnlyList<Track> _tracks;
    private readonly ILyricsBulkService _service;
    private readonly Func<IReadOnlyList<Track>, Task>? _openStudio;
    private readonly Dictionary<Guid, Row> _rowById = new();
    private CancellationTokenSource? _cts;
    private IReadOnlyList<Track> _plainOnly = Array.Empty<Track>();

    /// <summary>Most songs handed to Lyrics Studio at once, so its review stays manageable.</summary>
    internal const int StudioBatch = 40;

    public bool IsRemove { get; }
    public bool IsFetch => !IsRemove;
    /// <summary>Opened for the whole library (Settings > Tools), not a selection.</summary>
    public bool IsLibrary { get; }

    /// <summary>Also look up songs with plain or line-synced lyrics and save a richer find.</summary>
    [ObservableProperty] private bool _upgradeExisting;
    /// <summary>Ask again about songs an earlier run already looked up.</summary>
    [ObservableProperty] private bool _recheck;
    /// <summary>GitHub #195: look up first, save only the finds the user keeps.</summary>
    [ObservableProperty] private bool _reviewFirst;
    /// <summary>A review run ended: the rows with finds carry a checkbox and Save Selected saves the ticked ones.</summary>
    [ObservableProperty] private bool _isReviewing;
    [ObservableProperty] private string _applyText = string.Empty;
    /// <summary>After a run: songs left with plain lyrics only, which Lyrics Studio can time.</summary>
    [ObservableProperty] private bool _canTimeInStudio;
    [ObservableProperty] private string _studioOfferText = string.Empty;
    public string TitleText { get; }
    public string SubtitleText { get; }
    public string ActionText => IsRemove ? L("BulkLyrics.Remove") : L("BulkLyrics.Fetch");

    private static string L(string key) => Noctis.Localization.Loc.T(key);
    private static string L(string key, params object[] args) => Noctis.Localization.Loc.T(key, args);

    public ObservableCollection<Row> Rows { get; } = new();

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public bool CanStart => !IsRunning && !IsDone && !IsReviewing;

    public event EventHandler? Closed;

    public BulkLyricsViewModel(IReadOnlyList<Track> tracks, ILyricsBulkService service, bool remove,
        bool library = false, Func<IReadOnlyList<Track>, Task>? openStudio = null)
    {
        _tracks = tracks;
        _service = service;
        _openStudio = openStudio;
        IsRemove = remove;
        IsLibrary = library && !remove;
        var n = tracks.Count;
        var songs = n == 1 ? L("LibrarySongs.SummarySong") : L("LibrarySongs.SummarySongs", n);
        TitleText = remove ? L("BulkLyrics.TitleRemove", songs) : IsLibrary ? L("BulkLyrics.TitleLibrary", songs) : L("BulkLyrics.TitleFetch", songs);
        SubtitleText = remove ? L("BulkLyrics.SubtitleRemove") : L("BulkLyrics.SubtitleFetch");
        foreach (var t in tracks)
        {
            var row = new Row(t);
            Rows.Add(row);
            _rowById.TryAdd(t.Id, row);
        }
        StatusMessage = remove ? L("BulkLyrics.NothingRemovedYet") : L("BulkLyrics.Ready");
        RowStatusesLoaded = LoadRowStatusesAsync();
    }

    /// <summary>Completes once the rows' lyrics states have been read and posted to the UI thread.</summary>
    internal Task RowStatusesLoaded { get; }

    // Each row's lyrics state is a lyrics-store read (a file probe per track), so it runs off the
    // UI thread: a big selection opens the dialog at once instead of freezing before it shows.
    private Task LoadRowStatusesAsync()
    {
        var rows = Rows.ToArray();
        return Task.Run(() =>
        {
            var states = Array.ConvertAll(rows, r => Row.LyricsState(r.Track));
            Dispatcher.UIThread.Post(() =>
            {
                for (var i = 0; i < rows.Length; i++)
                    if (!rows[i].Done) rows[i].Status = states[i]; // a run already reported this row
            });
        });
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CloseText));
        OnPropertyChanged(nameof(CanChangeOptions));
        OnPropertyChanged(nameof(CanApply));
    }

    partial void OnIsDoneChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanChangeOptions));
    }

    partial void OnIsReviewingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanChangeOptions));
        OnPropertyChanged(nameof(CanApply));
    }

    /// <summary>Save Selected: while reviewing, not mid-save, with at least one find ticked.</summary>
    public bool CanApply => IsReviewing && !IsRunning && Rows.Any(r => r.CanChoose && r.IsChosen);

    private void RefreshApply()
    {
        var n = Rows.Count(r => r.CanChoose && r.IsChosen);
        ApplyText = L("BulkLyrics.SaveSelected", n);
        OnPropertyChanged(nameof(CanApply));
    }

    /// <summary>"Hide" while a run goes on (it keeps running), "Close" otherwise.</summary>
    public string CloseText => IsRunning ? L("BulkLyrics.Hide") : L("BulkLyrics.Close");
    public bool CanChangeOptions => IsFetch && !IsRunning && !IsDone && !IsReviewing;

    [RelayCommand]
    private async Task Start()
    {
        if (!CanStart) return;
        IsRunning = true;
        Progress = 0;
        StatusMessage = IsRemove ? L("BulkLyrics.Removing") : L("BulkLyrics.Fetching");
        _cts = new CancellationTokenSource();
        var progress = new Progress<LyricsBulkProgress>(p => Dispatcher.UIThread.Post(() =>
        {
            Progress = p.Total == 0 ? 1 : p.Done / (double)p.Total;
            var row = _rowById.GetValueOrDefault(p.TrackId)
                      ?? Rows.FirstOrDefault(r => r.Track.Title == p.CurrentTitle && !r.Done) ?? Rows.FirstOrDefault(r => !r.Done);
            if (row is not null) { row.Status = p.Outcome; row.Done = true; row.Failed = p.Outcome is "failed" or "not found" or "sources busy"; }
            if (IsRunning) StatusMessage = L(IsRemove ? "BulkLyrics.RemovingProgress" : "BulkLyrics.LookingUpProgress", p.Done, p.Total);
        }));
        try
        {
            if (IsRemove)
            {
                var removed = await _service.RemoveAsync(_tracks, progress, _cts.Token);
                StatusMessage = removed == 0 ? L("BulkLyrics.NothingToRemove") : Noctis.Localization.Loc.Plural("BulkLyrics.RemovedFrom", removed);
            }
            else
            {
                var s = await _service.FetchAsync(_tracks, new LyricsBulkOptions(UpgradeExisting, Recheck, ReviewOnly: ReviewFirst), progress, _cts.Token);
                if (ReviewFirst && s.Finds.Count > 0)
                {
                    // GitHub #195: nothing saved yet; the finds wait for Save Selected.
                    ShowFinds(s.Finds);
                    StatusMessage = Noctis.Localization.Loc.Plural("BulkLyrics.ReviewReady", s.Finds.Count);
                    IsReviewing = true;
                    return;
                }
                StatusMessage = L(_cts.IsCancellationRequested ? "BulkLyrics.StoppedSummary" : "BulkLyrics.DoneSummary", Summarize(s));
                OfferStudio(s.PlainOnlyTracks);
            }
            IsDone = true;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = L("BulkLyrics.Stopped");
        }
        catch (Exception ex)
        {
            StatusMessage = L("BulkLyrics.Failed", ex.Message);
        }
        finally
        {
            IsRunning = false;
            Progress = 1;
        }
    }

    // Each find goes on its song's row, ticked: unticking leaves that song as it is.
    private void ShowFinds(IReadOnlyList<LyricsBulkFind> finds)
    {
        foreach (var f in finds)
        {
            if (!_rowById.TryGetValue(f.Track.Id, out var row)) continue;
            row.Find = f;
            row.KindText = KindLabel(f.Rank);
            row.Status = f.Source;
            row.Done = true; // the background row-state read must not write over the source
            row.CanChoose = true;
            row.IsChosen = true;
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Row.IsChosen)) RefreshApply(); };
        }
        RefreshApply();
    }

    internal static string KindLabel(int rank) => rank >= Noctis.Services.LyricsSearchSelector.WordSyncedRank
        ? L("BulkLyrics.KindWord") : rank >= 2 ? L("BulkLyrics.KindLine") : L("BulkLyrics.KindPlain");

    /// <summary>GitHub #195: saves the ticked finds of a review run; the unticked songs stay as they were.</summary>
    [RelayCommand]
    private async Task Apply()
    {
        if (!CanApply) return;
        var chosen = Rows.Where(r => r.CanChoose && r.IsChosen && r.Find is not null).Select(r => r.Find!).ToList();
        foreach (var r in Rows.Where(r => r.CanChoose))
        {
            r.CanChoose = false;
            if (!r.IsChosen) r.Status = L("BulkLyrics.NotSaved");
        }
        IsRunning = true;
        Progress = 0;
        StatusMessage = L("BulkLyrics.Saving");
        _cts = new CancellationTokenSource();
        var progress = new Progress<LyricsBulkProgress>(p => Dispatcher.UIThread.Post(() =>
        {
            Progress = p.Total == 0 ? 1 : p.Done / (double)p.Total;
            if (_rowById.GetValueOrDefault(p.TrackId) is { } row) { row.Status = p.Outcome; row.Failed = p.Outcome == "failed"; }
        }));
        try
        {
            var s = await _service.ApplyAsync(chosen, progress, _cts.Token);
            StatusMessage = L("BulkLyrics.DoneSummary", Summarize(s));
            OfferStudio(s.PlainOnlyTracks);
        }
        catch (Exception ex)
        {
            StatusMessage = L("BulkLyrics.Failed", ex.Message);
        }
        finally
        {
            IsRunning = false;
            Progress = 1;
            IsReviewing = false;
            IsDone = true;
        }
    }

    internal static string Summarize(LyricsBulkSummary s)
    {
        var parts = new List<string>();
        if (s.WordSynced > 0) parts.Add(L("BulkLyrics.SumWordSynced", s.WordSynced));
        if (s.Synced - s.WordSynced > 0) parts.Add(L("BulkLyrics.SumLineSynced", s.Synced - s.WordSynced));
        if (s.PlainOnly > 0) parts.Add(L("BulkLyrics.SumPlainOnly", s.PlainOnly));
        if (s.Upgraded > 0) parts.Add(L("BulkLyrics.SumUpgraded", s.Upgraded));
        if (s.NotFound > 0) parts.Add(L("BulkLyrics.SumNotFound", s.NotFound));
        var kept = s.Skipped - s.CheckedBefore;
        if (kept > 0) parts.Add(L("BulkLyrics.SumKept", kept));
        if (s.CheckedBefore > 0) parts.Add(L("BulkLyrics.SumCheckedBefore", s.CheckedBefore));
        if (s.Failed > 0) parts.Add(L("BulkLyrics.SumFailed", s.Failed));
        if (s.KeptOwnFile > 0) parts.Add(L("BulkLyrics.SumKeptOwnFile", s.KeptOwnFile));
        if (s.BusySources.Count > 0) parts.Add(L("BulkLyrics.SumBusy", string.Join(", ", s.BusySources)));
        return parts.Count == 0 ? L("BulkLyrics.SumNothing") : string.Join(" · ", parts);
    }

    // Lyrics Studio can time plain lyrics on this PC with its speech model (Lullaby): offered,
    // never run on its own. It is slow, and every song is reviewed before it is saved.
    private void OfferStudio(IReadOnlyList<Track> plainOnly)
    {
        _plainOnly = plainOnly;
        CanTimeInStudio = _openStudio is not null && plainOnly.Count > 0;
        if (!CanTimeInStudio) return;
        var n = Math.Min(plainOnly.Count, StudioBatch);
        StudioOfferText = (plainOnly.Count == 1 ? L("BulkLyrics.StudioOfferOne") : L("BulkLyrics.StudioOfferMany", plainOnly.Count))
            + (plainOnly.Count > n ? " " + L("BulkLyrics.StudioOfferFirst", n) : string.Empty);
    }

    [RelayCommand]
    private async Task OpenStudio()
    {
        if (_openStudio is null || _plainOnly.Count == 0) return;
        var batch = _plainOnly.Take(StudioBatch).ToList();
        Closed?.Invoke(this, EventArgs.Empty);
        await _openStudio(batch);
    }

    /// <summary>Stops a run (what was saved stays saved; the next run resumes).</summary>
    [RelayCommand]
    private void Stop()
    {
        if (IsRunning) _cts?.Cancel();
    }

    /// <summary>Close, or while running hide: the run carries on in the background.</summary>
    [RelayCommand]
    private void Cancel() => Closed?.Invoke(this, EventArgs.Empty);

    public sealed partial class Row : ObservableObject
    {
        public Row(Track track)
        {
            Track = track;
        }

        internal static string LyricsState(Track track)
            => string.IsNullOrWhiteSpace(track.SyncedLyrics) ? (string.IsNullOrWhiteSpace(track.Lyrics) ? "no lyrics" : "plain lyrics") : "synced";

        public Track Track { get; }
        public string Title => Track.Title;
        public bool IsExplicit => Track.IsExplicit;
        public string Subtitle => Track.ArtistDisplay;
        [ObservableProperty] private string _status = string.Empty;
        [ObservableProperty, NotifyPropertyChangedFor(nameof(Succeeded))] private bool _done;
        [ObservableProperty, NotifyPropertyChangedFor(nameof(Succeeded))] private bool _failed;
        /// <summary>The row's tick: a song that ended "not found" or "failed" showed it beside the red outcome.</summary>
        public bool Succeeded => Done && !Failed;
        /// <summary>A review run's find for this song (GitHub #195); null when nothing better was found.</summary>
        public LyricsBulkFind? Find { get; set; }
        /// <summary>Word-synced / Line-synced / Plain: the find's format, shown as a badge.</summary>
        [ObservableProperty] private string _kindText = string.Empty;
        /// <summary>The checkbox shows while the user reviews this row's find.</summary>
        [ObservableProperty] private bool _canChoose;
        [ObservableProperty] private bool _isChosen;
        public bool HasKind => !string.IsNullOrEmpty(KindText);
        partial void OnKindTextChanged(string value) => OnPropertyChanged(nameof(HasKind));
    }
}
