using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;

namespace Noctis.ViewModels;

/// <summary>
/// Search-driven library picker for adding songs to a playlist. Results are populated
/// only while searching (and capped) so it stays responsive over large libraries.
/// </summary>
public partial class AddSongsDialogViewModel : ViewModelBase
{
    private const int MaxResults = 100;
    private const int ShuffledPickCount = 30;

    private readonly IReadOnlyList<Track> _library;
    private readonly HashSet<Guid> _alreadyInPlaylist;
    private readonly HashSet<Guid> _selected = new();

    /// <summary>
    /// Tick order, so Add() appends in the order the user chose rather than in library
    /// order — `_library.Where(t => _selected.Contains(t.Id))` yields library order, so
    /// deliberately ticking five songs in sequence produced an unrelated one.
    /// </summary>
    private readonly List<Guid> _selectionOrder = new();
    private List<Track> _shuffledPicks = new();

    /// <summary>Every track the current query matches, before the row cap is applied.</summary>
    private List<Track> _matches = new();

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private int _selectedCount;

    public ObservableCollection<AddSongItem> Results { get; } = new();

    private readonly string? _playlistName;

    /// <summary>"Add songs to Road Trip" (plain "Add Songs" when the caller gave no name).</summary>
    public string Title => string.IsNullOrWhiteSpace(_playlistName)
        ? Localization.Loc.T("AddSongs.AddSongs")
        : Localization.Loc.T("AddSongs.TitleFor", _playlistName.Trim());

    /// <summary>How many of the library's songs the playlist already has (what its page lists).</summary>
    public int InPlaylistCount { get; }

    public string Subtitle => InPlaylistCount switch
    {
        0 => Localization.Loc.T("AddSongs.PlaylistEmpty"),
        1 => Localization.Loc.T("AddSongs.PlaylistOneSong"),
        _ => Localization.Loc.T("AddSongs.PlaylistSongCount", InPlaylistCount),
    };

    /// <summary>True while the search box is empty and shuffled library picks are shown.</summary>
    public bool IsShuffleMode => string.IsNullOrWhiteSpace(SearchText) && _shuffledPicks.Count > 0;
    /// <summary>Nothing to suggest because every library song is already in the playlist.</summary>
    public bool ShowPrompt => string.IsNullOrWhiteSpace(SearchText) && _shuffledPicks.Count == 0 && !IsLibraryEmpty;
    /// <summary>Nothing to suggest because the library has no songs (the prompt above said they
    /// were all in the playlist).</summary>
    public bool ShowEmptyLibrary => string.IsNullOrWhiteSpace(SearchText) && IsLibraryEmpty;
    public bool IsLibraryEmpty => _library.Count == 0;
    public bool ShowNoResults => !string.IsNullOrWhiteSpace(SearchText) && Results.Count == 0;
    public bool HasSelection => _selected.Count > 0;
    public string SelectionText => Localization.Loc.T("AddSongs.SelectedCount", _selected.Count);
    public string AddButtonText => _selected.Count > 0
        ? Localization.Loc.T("AddSongs.AddCount", _selected.Count)
        : Localization.Loc.T("AddSongs.Add");

    /// <summary>
    /// How many tracks the query matches, ignoring the row cap. At most MaxResults rows
    /// are rendered, but every count the user reads — this notice, select-all, the Add
    /// button — speaks for the whole match set: 113 songs by one band rendered as
    /// "Add 100" with nothing saying 13 were missing, and read as a playlist limit.
    /// </summary>
    public int MatchCount => _matches.Count;
    public bool IsTruncated => _matches.Count > Results.Count;
    public string TruncationNotice => Localization.Loc.T("AddSongs.Showing", Results.Count, _matches.Count);

    /// <summary>Tracks the user can actually tick (ones already in the playlist can't).</summary>
    private IEnumerable<Track> SelectableMatches => _matches.Where(t => !_alreadyInPlaylist.Contains(t.Id));

    /// <summary>Drives the select-all button: hidden when there is nothing to tick.</summary>
    public bool HasSelectableResults => SelectableMatches.Any();
    public bool AreAllResultsSelected => HasSelectableResults && SelectableMatches.All(t => _selected.Contains(t.Id));

    /// <summary>Names the count only when it exceeds what is on screen — otherwise
    /// "Select all" already means the visible rows.</summary>
    public string SelectAllText => AreAllResultsSelected
        ? Localization.Loc.T("AddSongs.Clear")
        : IsTruncated
            ? Localization.Loc.T("AddSongs.SelectAllCount", SelectableMatches.Count())
            : Localization.Loc.T("AddSongs.SelectAll");

    /// <summary>Fires with the chosen tracks when the user confirms.</summary>
    public event EventHandler<IReadOnlyList<Track>>? SongsChosen;

    /// <summary>Fires when the dialog should close.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Set once Add has handed the songs over; a second Add while the dialog animates
    /// out does nothing.</summary>
    private bool _added;

    public AddSongsDialogViewModel(IReadOnlyList<Track> library, IEnumerable<Guid> alreadyInPlaylist, string? playlistName = null)
    {
        _library = library ?? Array.Empty<Track>();
        _alreadyInPlaylist = new HashSet<Guid>(alreadyInPlaylist ?? Enumerable.Empty<Guid>());
        _playlistName = playlistName;
        InPlaylistCount = _library.Where(t => _alreadyInPlaylist.Contains(t.Id)).Select(t => t.Id).Distinct().Count();
        BuildShuffledPicks();
        RefreshResults();
    }

    // Debounced. RefreshResults filters the whole library on the UI thread, and
    // MatchesSearch normalizes title/artist/album per track, so a query scanned all
    // 50,000 tracks per keystroke while the user was still typing. Every other search
    // surface in the app already debounces (Songs/Albums/Artists 250ms, top bar 300ms).
    // The scan is now unconditional — reporting an exact match count rules out the
    // Take(MaxResults) short-circuit, which only helped broad queries anyway — so the
    // debounce carries the cost that the cap used to soften.
    private const int SearchDebounceMs = 250;
    private CancellationTokenSource? _searchDebounceCts;

    partial void OnSearchTextChanged(string value)
    {
        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _searchDebounceCts = cts;
        _ = DebouncedRefreshAsync(cts.Token);
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

    /// <summary>Random library sample (tracks not already in the playlist), shown before any search.</summary>
    private void BuildShuffledPicks()
    {
        _shuffledPicks = _library
            .Where(t => !_alreadyInPlaylist.Contains(t.Id))
            .OrderBy(_ => Random.Shared.Next())
            .Take(ShuffledPickCount)
            .ToList();
    }

    [RelayCommand]
    private void Reshuffle()
    {
        BuildShuffledPicks();
        RefreshResults();
    }

    private void RefreshResults()
    {
        Results.Clear();

        var query = (SearchText ?? string.Empty).Trim();
        _matches = query.Length > 0
            ? _library.Where(Noctis.Helpers.SearchQuery.Parse(query).Matches).ToList()
            : _shuffledPicks;

        foreach (var track in _matches.Take(MaxResults))
        {
            Results.Add(new AddSongItem(track)
            {
                IsInPlaylist = _alreadyInPlaylist.Contains(track.Id),
                IsSelected = _selected.Contains(track.Id)
            });
        }

        OnPropertyChanged(nameof(IsShuffleMode));
        OnPropertyChanged(nameof(ShowPrompt));
        OnPropertyChanged(nameof(ShowEmptyLibrary));
        OnPropertyChanged(nameof(ShowNoResults));
        OnPropertyChanged(nameof(MatchCount));
        OnPropertyChanged(nameof(IsTruncated));
        OnPropertyChanged(nameof(TruncationNotice));
        OnPropertyChanged(nameof(HasSelectableResults));
        OnPropertyChanged(nameof(AreAllResultsSelected));
        OnPropertyChanged(nameof(SelectAllText));
    }

    [RelayCommand]
    private void ToggleSelect(AddSongItem? item)
    {
        if (item == null || item.IsInPlaylist) return;

        if (_selected.Add(item.Track.Id))
        {
            _selectionOrder.Add(item.Track.Id);
            item.IsSelected = true;
        }
        else
        {
            _selected.Remove(item.Track.Id);
            _selectionOrder.Remove(item.Track.Id);
            item.IsSelected = false;
        }

        RaiseSelectionChanged();
    }

    /// <summary>
    /// Ticks every track the query matches, or clears them when they are all already
    /// ticked. Scoped to the matches rather than the rendered rows — the row cap is a
    /// display budget, and stopping at it silently dropped the songs past row 100.
    /// The button names the count whenever the two differ, so the wider reach is never
    /// a surprise.
    /// </summary>
    [RelayCommand]
    private void ToggleSelectAll()
    {
        var selectable = SelectableMatches.ToList();
        if (selectable.Count == 0) return;

        if (selectable.All(t => _selected.Contains(t.Id)))
        {
            foreach (var track in selectable)
            {
                _selected.Remove(track.Id);
                _selectionOrder.Remove(track.Id);
            }
        }
        else
        {
            // Append in match order, keeping the tick-order contract Add() relies on.
            foreach (var track in selectable)
            {
                if (_selected.Add(track.Id))
                    _selectionOrder.Add(track.Id);
            }
        }

        foreach (var row in Results)
            row.IsSelected = _selected.Contains(row.Track.Id);

        RaiseSelectionChanged();
    }

    private void RaiseSelectionChanged()
    {
        SelectedCount = _selected.Count;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(AddButtonText));
        OnPropertyChanged(nameof(AreAllResultsSelected));
        OnPropertyChanged(nameof(SelectAllText));
    }

    [RelayCommand]
    private void Add()
    {
        if (_added) return;
        if (_selected.Count > 0)
        {
            _added = true;
            // Projected in tick order (see _selectionOrder), not library order.
            var byId = _library.Where(t => _selected.Contains(t.Id))
                .GroupBy(t => t.Id)
                .ToDictionary(g => g.Key, g => g.First());

            var chosen = _selectionOrder
                .Where(byId.ContainsKey)
                .Select(id => byId[id])
                .ToList();

            SongsChosen?.Invoke(this, chosen);
        }
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);
}

/// <summary>One row in the Add Songs picker.</summary>
public partial class AddSongItem : ObservableObject
{
    public AddSongItem(Track track) => Track = track;

    public Track Track { get; }

    /// <summary>True when the track is already in the target playlist (tagged "In playlist", not tickable).</summary>
    public bool IsInPlaylist { get; set; }

    [ObservableProperty] private bool _isSelected;
}
