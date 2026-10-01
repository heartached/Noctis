using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Loc = Noctis.Localization.Loc;

namespace Noctis.ViewModels;

/// <summary>
/// Search Lyrics with a source picker (issue #113): searches one chosen source, or "Auto" —
/// the sources the automatic lookup uses — lists what each found and in which format, previews
/// the selected answer and applies it to the lyrics page. Auto marks the answer the automatic
/// lookup would pick (<see cref="LyricsViewModel.PickBestResult"/>: word-synced over
/// line-synced over plain, the earlier source on a tie).
/// </summary>
public partial class LyricsSearchViewModel : ViewModelBase
{
    private readonly Func<Task<IReadOnlyList<string>>> _autoSources;
    private readonly Func<IReadOnlyList<string>, string, string, CancellationToken, Task<IReadOnlyList<LyricsSourceHit>>> _search;
    private readonly Action<LrcLibResult, string> _apply;
    private CancellationTokenSource? _searchCts;
    private int _generation;

    /// <param name="track">The song the lyrics are for (artist / title prefill the search).</param>
    /// <param name="sources">Every source the picker offers, in priority order.</param>
    /// <param name="autoSources">The sources "Auto" searches, read at search time.</param>
    /// <param name="search">Searches the given sources for (artist, title); one hit per source, in order.</param>
    /// <param name="apply">Shows and keeps the chosen answer.</param>
    public LyricsSearchViewModel(
        Track track,
        IReadOnlyList<string> sources,
        Func<Task<IReadOnlyList<string>>> autoSources,
        Func<IReadOnlyList<string>, string, string, CancellationToken, Task<IReadOnlyList<LyricsSourceHit>>> search,
        Action<LrcLibResult, string> apply)
    {
        _autoSources = autoSources;
        _search = search;
        _apply = apply;
        AutoLabel = Loc.T("LyricsSearch.Auto");
        Sources = new[] { AutoLabel }.Concat(sources).ToList();
        _selectedSource = AutoLabel;
        _artist = LyricsSearchSelector.IsUnknownArtist(track.Artist) ? string.Empty : track.Artist ?? string.Empty;
        _title = track.Title ?? string.Empty;
        TrackLabel = string.IsNullOrWhiteSpace(track.Artist) ? track.Title ?? string.Empty : $"{track.Title} — {track.Artist}";
    }

    /// <summary>The picker's first entry: search every Auto source and mark the best answer.</summary>
    public string AutoLabel { get; }

    public IReadOnlyList<string> Sources { get; }

    public string TrackLabel { get; }

    public ObservableCollection<LyricsSearchResultRow> Results { get; } = new();

    [ObservableProperty] private string _selectedSource;
    [ObservableProperty] private string _artist;
    [ObservableProperty] private string _title;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string _statusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewText), nameof(HasPreview))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private LyricsSearchResultRow? _selectedResult;

    public string PreviewText => SelectedResult?.PreviewText ?? string.Empty;
    public bool HasPreview => !string.IsNullOrWhiteSpace(PreviewText);

    public event EventHandler? Closed;

    private bool IsAuto => string.IsNullOrEmpty(SelectedSource) || SelectedSource == AutoLabel;

    partial void OnSelectedSourceChanged(string value) => _ = SearchAsync();

    [RelayCommand]
    private Task Search() => SearchAsync();

    /// <summary>Runs the search for the picked source; a newer search supersedes (and cancels) an older one.</summary>
    internal async Task SearchAsync()
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        var generation = ++_generation;
        var auto = IsAuto;
        IsSearching = true;
        StatusText = Loc.T("LyricsSearch.Searching");
        Results.Clear();
        SelectedResult = null;
        try
        {
            var sources = auto ? await _autoSources() : new[] { SelectedSource };
            if (generation != _generation) return;
            if (sources.Count == 0)
            {
                StatusText = Loc.T("LyricsSearch.NoSources");
                return;
            }

            var hits = await _search(sources, Artist.Trim(), Title.Trim(), cts.Token);
            if (generation != _generation) return;

            var (best, bestSource, _, _) = LyricsViewModel.PickBestResult(hits.Select(h => (h.Result, h.Source)).ToList());
            foreach (var hit in hits)
                Results.Add(new LyricsSearchResultRow(hit, isBest: auto && best != null && ReferenceEquals(hit.Result, best)));
            SelectedResult = Results.FirstOrDefault(r => r.IsBest) ?? Results.FirstOrDefault(r => r.HasLyrics);

            StatusText = best != null
                ? (auto ? Loc.T("LyricsSearch.BestMatch", bestSource) : string.Empty)
                : hits.Count > 0 && hits.All(h => h.Errored)
                    ? Loc.T("LyricsSearch.Failed")
                    : Loc.T("LyricsSearch.NotFound");
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer search, or the dialog closed.
        }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.Lyrics, "LyricsSearch:Unhandled", ex.Message);
            if (generation == _generation) StatusText = Loc.T("LyricsSearch.Failed");
        }
        finally
        {
            if (generation == _generation) IsSearching = false;
        }
    }

    private bool CanApply() => SelectedResult is { HasLyrics: true };

    /// <summary>Shows the selected answer on the lyrics page and keeps it, then closes.</summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (SelectedResult is not { Result: { HasLyrics: true } result } row) return;
        _apply(result, row.Source);
        Close();
    }

    [RelayCommand]
    private void Close()
    {
        _searchCts?.Cancel();
        Closed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>One source's answer in the Search Lyrics list.</summary>
public sealed class LyricsSearchResultRow
{
    public LyricsSearchResultRow(LyricsSourceHit hit, bool isBest)
    {
        Source = hit.Source;
        Result = hit.Result;
        IsBest = isBest;

        var rank = LyricsSearchSelector.FormatRank(hit.Result);
        FormatLabel = rank switch
        {
            >= LyricsSearchSelector.WordSyncedRank => Loc.T("LyricsSearch.WordSynced"),
            2 => Loc.T("LyricsSearch.LineSynced"),
            1 => Loc.T("LyricsSearch.Plain"),
            _ when hit.Errored => Loc.T("LyricsSearch.CouldNotConnect"),
            _ when hit.Result is { Instrumental: true } => Loc.T("LyricsSearch.Instrumental"),
            _ => Loc.T("LyricsSearch.NoMatch"),
        };

        if (HasLyrics)
        {
            var r = hit.Result!;
            var name = string.IsNullOrWhiteSpace(r.ArtistName) ? r.TrackName : $"{r.TrackName} — {r.ArtistName}";
            Detail = r.Duration > 0 ? $"{name} · {TimeSpan.FromSeconds(r.Duration):m\\:ss}" : name ?? string.Empty;
            PreviewText = r.HasSyncedLyrics
                ? LyricsTextHelper.StripTimestamps(r.SyncedLyrics)
                : r.PlainLyrics ?? string.Empty;
        }
    }

    public string Source { get; }
    public LrcLibResult? Result { get; }
    public bool IsBest { get; }
    public bool HasLyrics => Result is { HasLyrics: true };

    /// <summary>"Word-synced", "Synced", "Plain", or why there is nothing.</summary>
    public string FormatLabel { get; }

    /// <summary>What the source matched: "Title — Artist · m:ss" (empty without lyrics).</summary>
    public string Detail { get; } = string.Empty;

    public string PreviewText { get; } = string.Empty;
}
