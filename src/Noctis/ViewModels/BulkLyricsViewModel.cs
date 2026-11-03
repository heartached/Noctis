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
    /// <summary>After a run: songs left with plain lyrics only, which Lyrics Studio can time.</summary>
    [ObservableProperty] private bool _canTimeInStudio;
    [ObservableProperty] private string _studioOfferText = string.Empty;
    public string TitleText { get; }
    public string SubtitleText { get; }
    public string ActionText => IsRemove ? "Remove" : "Fetch";

    public ObservableCollection<Row> Rows { get; } = new();

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public bool CanStart => !IsRunning && !IsDone;

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
        var songs = n == 1 ? "1 song" : $"{n:N0} songs";
        TitleText = remove ? $"Remove lyrics from {songs}" : IsLibrary ? $"Find lyrics for your library ({songs})" : $"Fetch lyrics for {songs}";
        SubtitleText = remove
            ? "Deletes the lyrics Noctis saved for these songs (its .lrc files and cached lyrics). Lyrics files you made yourself are left alone."
            : "Finds lyrics online, word-synced first, and saves them next to your songs. Your own lyrics files are never replaced.";
        foreach (var t in tracks)
        {
            var row = new Row(t);
            Rows.Add(row);
            _rowById.TryAdd(t.Id, row);
        }
        StatusMessage = remove ? "Nothing removed yet." : "Ready.";
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
    }

    partial void OnIsDoneChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanChangeOptions));
    }

    /// <summary>"Hide" while a run goes on (it keeps running), "Close" otherwise.</summary>
    public string CloseText => IsRunning ? "Hide" : "Close";
    public bool CanChangeOptions => IsFetch && !IsRunning && !IsDone;

    [RelayCommand]
    private async Task Start()
    {
        if (!CanStart) return;
        IsRunning = true;
        Progress = 0;
        StatusMessage = IsRemove ? "Removing" : "Fetching";
        _cts = new CancellationTokenSource();
        var progress = new Progress<LyricsBulkProgress>(p => Dispatcher.UIThread.Post(() =>
        {
            Progress = p.Total == 0 ? 1 : p.Done / (double)p.Total;
            var row = _rowById.GetValueOrDefault(p.TrackId)
                      ?? Rows.FirstOrDefault(r => r.Track.Title == p.CurrentTitle && !r.Done) ?? Rows.FirstOrDefault(r => !r.Done);
            if (row is not null) { row.Status = p.Outcome; row.Done = true; row.Failed = p.Outcome is "failed" or "not found" or "sources busy"; }
            if (IsRunning) StatusMessage = $"{(IsRemove ? "Removing" : "Looking up")} {p.Done:N0} of {p.Total:N0}";
        }));
        try
        {
            if (IsRemove)
            {
                var removed = await _service.RemoveAsync(_tracks, progress, _cts.Token);
                StatusMessage = removed == 0 ? "No lyrics to remove." : $"Removed lyrics from {removed} song{(removed == 1 ? "" : "s")}.";
            }
            else
            {
                var s = await _service.FetchAsync(_tracks, new LyricsBulkOptions(UpgradeExisting, Recheck), progress, _cts.Token);
                StatusMessage = (_cts.IsCancellationRequested ? "Stopped · " : "Done · ") + Summarize(s);
                OfferStudio(s.PlainOnlyTracks);
            }
            IsDone = true;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Stopped.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed — {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            Progress = 1;
        }
    }

    internal static string Summarize(LyricsBulkSummary s)
    {
        var parts = new List<string>();
        if (s.WordSynced > 0) parts.Add($"{s.WordSynced:N0} word-synced");
        if (s.Synced - s.WordSynced > 0) parts.Add($"{s.Synced - s.WordSynced:N0} line-synced");
        if (s.PlainOnly > 0) parts.Add($"{s.PlainOnly:N0} plain only");
        if (s.Upgraded > 0) parts.Add($"{s.Upgraded:N0} upgraded");
        if (s.NotFound > 0) parts.Add($"{s.NotFound:N0} not found");
        var kept = s.Skipped - s.CheckedBefore;
        if (kept > 0) parts.Add($"{kept:N0} kept as they were");
        if (s.CheckedBefore > 0) parts.Add($"{s.CheckedBefore:N0} looked up before");
        if (s.Failed > 0) parts.Add($"{s.Failed:N0} failed");
        if (s.KeptOwnFile > 0) parts.Add($"{s.KeptOwnFile:N0} kept your own lyrics file");
        if (s.BusySources.Count > 0) parts.Add($"{string.Join(", ", s.BusySources)} busy, try again later");
        return parts.Count == 0 ? "nothing to do" : string.Join(" · ", parts);
    }

    // Lyrics Studio can time plain lyrics on this PC with its speech model (Lullaby): offered,
    // never run on its own. It is slow, and every song is reviewed before it is saved.
    private void OfferStudio(IReadOnlyList<Track> plainOnly)
    {
        _plainOnly = plainOnly;
        CanTimeInStudio = _openStudio is not null && plainOnly.Count > 0;
        if (!CanTimeInStudio) return;
        var n = Math.Min(plainOnly.Count, StudioBatch);
        StudioOfferText = (plainOnly.Count == 1 ? "1 song has" : $"{plainOnly.Count:N0} songs have")
            + " plain lyrics only. Lyrics Studio can time them on this PC with its speech model: it can take a minute or more per song"
            + " (faster on a graphics card), and you check each one before it's saved."
            + (plainOnly.Count > n ? $" The first {n} open at a time." : string.Empty);
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
        [ObservableProperty] private bool _done;
        [ObservableProperty] private bool _failed;
    }
}
