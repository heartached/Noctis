using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.LyricsStudio;

namespace Noctis.ViewModels;

/// <summary>What the picker hands back: the songs, in pick order, and the format to write.</summary>
public sealed record LyricsStudioPick(IReadOnlyList<Track> Tracks, bool WordTimings);

/// <summary>One search result: a song, or an album standing for every local song it holds.</summary>
public sealed partial class LyricsStudioPickRow : ObservableObject
{
    public required bool IsAlbum { get; init; }
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public string? ArtworkPath { get; init; }
    /// <summary>The local songs this row stands for (one for a song row).</summary>
    public required IReadOnlyList<Track> Tracks { get; init; }

    [ObservableProperty] private bool _isSelected;
    /// <summary>Song rows: what the song has now (word-level, line-level, plain only, no lyrics). Album rows: the song count.</summary>
    [ObservableProperty] private string _stateText = string.Empty;
}

/// <summary>
/// Lyrics Studio › Choose songs (user ask 09-19): search the library for songs and albums,
/// tick the ones to time, and pick the format (word timings = ELRC, line timings = LRC).
/// With nothing typed it lists the songs that lack the chosen format (the page's own cached
/// scan), so the usual case is tick, or Select all, and go. The page then runs the Studio
/// over exactly those songs instead of its own "first 40 that lack the format" pick.
/// </summary>
public partial class LyricsStudioPickerViewModel : ObservableObject
{
    private const int MaxAlbumRows = 20;
    private const int MaxTrackRows = 80;

    private readonly ILibraryService _library;
    private readonly Func<IReadOnlyList<Track>, IReadOnlyList<LyricsFormat>> _detectFormats;
    private readonly Func<bool, Task<IReadOnlyList<Track>>>? _suggest;
    private readonly HashSet<Guid> _selectedIds = new();
    private readonly List<Track> _picked = new();
    private IReadOnlyList<Track> _suggestions = Array.Empty<Track>();
    private int _scanGeneration;
    private int _suggestGeneration;

    public ObservableCollection<LyricsStudioPickRow> Results { get; } = new();

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _wordTimings;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private bool _isLoadingSuggestions;

    public bool LineTimings
    {
        get => !WordTimings;
        set => WordTimings = !value;
    }

    private bool QueryEmpty => string.IsNullOrWhiteSpace(SearchText);
    /// <summary>Nothing typed and the library's missing-format songs are on show.</summary>
    public bool IsSuggesting => QueryEmpty && _suggestions.Count > 0;
    public string SuggestionsHeader => Localization.Loc.T(WordTimings ? "LyricsStudioPicker.MissingWord" : "LyricsStudioPicker.MissingLine");
    public bool ShowPrompt => QueryEmpty && _suggestions.Count == 0 && !IsLoadingSuggestions;
    public bool ShowNoResults => !QueryEmpty && Results.Count == 0;
    public bool HasSelection => SelectedCount > 0;
    public string SelectionText => SelectedCount == 1 ? "1 song selected" : $"{SelectedCount} songs selected";
    public string AddButtonText => SelectedCount == 0
        ? Localization.Loc.T("LyricsStudioPicker.Add")
        : $"{Localization.Loc.T("LyricsStudioPicker.Add")} ({SelectedCount})";

    /// <summary>Select all covers the song rows on show (album rows are shortcuts for their songs).</summary>
    public bool HasSelectableResults => Results.Any(r => !r.IsAlbum);
    public bool AreAllResultsSelected => HasSelectableResults && Results.Where(r => !r.IsAlbum).All(r => r.IsSelected);
    public string SelectAllText => Localization.Loc.T(AreAllResultsSelected ? "LyricsStudioPicker.DeselectAll" : "LyricsStudioPicker.SelectAll");

    /// <summary>The last format scan kicked off by a search (tests await it).</summary>
    internal Task FormatScan { get; private set; } = Task.CompletedTask;
    /// <summary>The last suggestions load (tests await it).</summary>
    internal Task SuggestionsLoad { get; private set; } = Task.CompletedTask;

    public event EventHandler<LyricsStudioPick>? Confirmed;
    public event EventHandler? CloseRequested;

    /// <param name="suggest">Songs that lack the format (word timings = true) to show before any search; null = search only.</param>
    public LyricsStudioPickerViewModel(ILibraryService library, bool wordTimings,
        Func<IReadOnlyList<Track>, IReadOnlyList<LyricsFormat>>? detectFormats = null,
        Func<bool, Task<IReadOnlyList<Track>>>? suggest = null)
    {
        _library = library;
        _wordTimings = wordTimings;
        _detectFormats = detectFormats ?? (tracks => ExistingLyricsLoader.DetectFormats(tracks));
        _suggest = suggest;
        SuggestionsLoad = LoadSuggestionsAsync();
    }

    // Debounced, like Add Songs: RefreshResults scans the whole library on the UI thread
    // (Take only stops early on a broad query), and it ran for every character typed.
    private const int SearchDebounceMs = 250;
    private CancellationTokenSource? _searchDebounceCts;

    /// <summary>The last debounced search refresh (tests await it).</summary>
    internal Task SearchRefresh { get; private set; } = Task.CompletedTask;

    partial void OnSearchTextChanged(string value)
    {
        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _searchDebounceCts = cts;
        SearchRefresh = DebouncedRefreshAsync(cts.Token);
    }

    private async Task DebouncedRefreshAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(SearchDebounceMs, token);
            if (token.IsCancellationRequested) return;
            RefreshResults();
        }
        catch (OperationCanceledException) { /* superseded by a newer keystroke */ }
    }

    partial void OnWordTimingsChanged(bool value)
    {
        OnPropertyChanged(nameof(LineTimings));
        OnPropertyChanged(nameof(SuggestionsHeader));
        // The format decides which songs count as missing.
        SuggestionsLoad = LoadSuggestionsAsync();
    }

    partial void OnSelectedCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(AddButtonText));
    }

    partial void OnIsLoadingSuggestionsChanged(bool value) => OnPropertyChanged(nameof(ShowPrompt));

    private async Task LoadSuggestionsAsync()
    {
        if (_suggest is null) return;
        var generation = ++_suggestGeneration;
        IsLoadingSuggestions = true;
        IReadOnlyList<Track> list;
        try { list = await _suggest(WordTimings); }
        catch { list = Array.Empty<Track>(); }
        if (generation != _suggestGeneration) return;
        _suggestions = list;
        IsLoadingSuggestions = false;
        if (QueryEmpty) RefreshResults();
    }

    private void RefreshResults()
    {
        Results.Clear();
        var query = (SearchText ?? string.Empty).Trim();
        if (query.Length == 0)
        {
            foreach (var track in _suggestions.Take(MaxTrackRows))
                Results.Add(RowForTrack(track));
            ScanFormats();
        }
        else
        {
            // Normalize the query once and match the cached keys, not every field per row.
            var queryKey = Noctis.Helpers.SearchText.Normalize(query);
            foreach (var album in _library.Albums
                         .Where(a => Noctis.Helpers.SearchText.Matches(a.Name, a.SearchNameKey, query, queryKey)
                                  || Noctis.Helpers.SearchText.Matches(a.Artist, a.SearchArtistKey, query, queryKey))
                         .Take(MaxAlbumRows))
            {
                var local = (album.Tracks ?? new List<Track>()).Where(t => t.SourceType == SourceType.Local).ToList();
                if (local.Count == 0) continue;
                Results.Add(new LyricsStudioPickRow
                {
                    IsAlbum = true,
                    Title = album.Name,
                    Subtitle = album.Artist,
                    ArtworkPath = album.ArtworkPath,
                    Tracks = local,
                    StateText = local.Count == 1 ? "1 song" : $"{local.Count} songs",
                    IsSelected = local.All(t => _selectedIds.Contains(t.Id)),
                });
            }
            foreach (var track in _library.Tracks
                         .Where(t => t.SourceType == SourceType.Local && PlaylistViewModel.MatchesSearch(t, query, queryKey))
                         .Take(MaxTrackRows))
                Results.Add(RowForTrack(track));
            ScanFormats();
        }
        RaiseListState();
    }

    private LyricsStudioPickRow RowForTrack(Track track) => new()
    {
        IsAlbum = false,
        Title = track.TitleDisplay,
        Subtitle = string.IsNullOrEmpty(track.Album) ? track.ArtistDisplay : $"{track.ArtistDisplay} · {track.Album}",
        ArtworkPath = track.AlbumArtworkPath,
        Tracks = new[] { track },
        IsSelected = _selectedIds.Contains(track.Id),
    };

    private void RaiseListState()
    {
        OnPropertyChanged(nameof(IsSuggesting));
        OnPropertyChanged(nameof(ShowPrompt));
        OnPropertyChanged(nameof(ShowNoResults));
        RaiseSelectAllState();
    }

    private void RaiseSelectAllState()
    {
        OnPropertyChanged(nameof(HasSelectableResults));
        OnPropertyChanged(nameof(AreAllResultsSelected));
        OnPropertyChanged(nameof(SelectAllText));
    }

    /// <summary>Format detection reads sidecars from disk, so it runs off the UI thread and
    /// fills the song rows in when it lands; a newer search discards an older scan.</summary>
    private void ScanFormats()
    {
        var rows = Results.Where(r => !r.IsAlbum).ToList();
        if (rows.Count == 0) return;
        var generation = ++_scanGeneration;
        var tracks = rows.Select(r => r.Tracks[0]).ToList();
        FormatScan = Task.Run(() => _detectFormats(tracks)).ContinueWith(t =>
        {
            if (t.Status != TaskStatus.RanToCompletion || generation != _scanGeneration) return;
            var formats = t.Result;
            void Apply()
            {
                if (generation != _scanGeneration) return;
                for (var i = 0; i < rows.Count && i < formats.Count; i++)
                    rows[i].StateText = StateLabel(formats[i]);
            }
            if (Dispatcher.UIThread.CheckAccess()) Apply(); else Dispatcher.UIThread.Post(Apply);
        }, TaskScheduler.Default);
    }

    /// <summary>Same wording as the Studio's own song list pills.</summary>
    internal static string StateLabel(LyricsFormat format) => LyricsStudioViewModel.StudioItem.FormatTag(format);

    [RelayCommand]
    private void ToggleSelect(LyricsStudioPickRow? row)
    {
        if (row is null) return;
        var allIn = row.Tracks.All(t => _selectedIds.Contains(t.Id));
        if (allIn) Remove(row.Tracks); else Add(row.Tracks);
        SyncSelection();
    }

    [RelayCommand]
    private void ToggleSelectAll()
    {
        var songs = Results.Where(r => !r.IsAlbum).SelectMany(r => r.Tracks).ToList();
        if (songs.Count == 0) return;
        if (songs.All(t => _selectedIds.Contains(t.Id))) Remove(songs); else Add(songs);
        SyncSelection();
    }

    private void Add(IEnumerable<Track> tracks)
    {
        foreach (var t in tracks)
            if (_selectedIds.Add(t.Id)) _picked.Add(t);
    }

    private void Remove(IEnumerable<Track> tracks)
    {
        var ids = tracks.Select(t => t.Id).ToHashSet();
        _selectedIds.ExceptWith(ids);
        _picked.RemoveAll(t => ids.Contains(t.Id));
    }

    private void SyncSelection()
    {
        foreach (var r in Results)
            r.IsSelected = r.Tracks.All(t => _selectedIds.Contains(t.Id));
        SelectedCount = _selectedIds.Count;
        RaiseSelectAllState();
    }

    /// <summary>The songs ticked so far, in the order they were ticked.</summary>
    public IReadOnlyList<Track> PickedTracks => _picked;

    [RelayCommand]
    private void Confirm()
    {
        if (_picked.Count == 0) return;
        Confirmed?.Invoke(this, new LyricsStudioPick(_picked.ToList(), WordTimings));
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
