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

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private int _selectedCount;

    public ObservableCollection<AddSongItem> Results { get; } = new();

    /// <summary>True while the search box is empty and shuffled library picks are shown.</summary>
    public bool IsShuffleMode => string.IsNullOrWhiteSpace(SearchText) && _shuffledPicks.Count > 0;
    public bool ShowPrompt => string.IsNullOrWhiteSpace(SearchText) && _shuffledPicks.Count == 0;
    public bool ShowNoResults => !string.IsNullOrWhiteSpace(SearchText) && Results.Count == 0;
    public bool HasSelection => _selected.Count > 0;
    public string AddButtonText => _selected.Count > 0 ? $"Add {_selected.Count}" : "Add";

    /// <summary>Rows the user can actually tick (tracks already in the playlist can't).</summary>
    private IEnumerable<AddSongItem> SelectableResults => Results.Where(r => !r.IsInPlaylist);

    /// <summary>Drives the select-all button: hidden when there is nothing to tick.</summary>
    public bool HasSelectableResults => SelectableResults.Any();
    public bool AreAllResultsSelected => HasSelectableResults && SelectableResults.All(r => r.IsSelected);
    public string SelectAllText => AreAllResultsSelected ? "Deselect all" : "Select all";

    /// <summary>Fires with the chosen tracks when the user confirms.</summary>
    public event EventHandler<IReadOnlyList<Track>>? SongsChosen;

    /// <summary>Fires when the dialog should close.</summary>
    public event EventHandler? CloseRequested;

    public AddSongsDialogViewModel(IReadOnlyList<Track> library, IEnumerable<Guid> alreadyInPlaylist)
    {
        _library = library ?? Array.Empty<Track>();
        _alreadyInPlaylist = new HashSet<Guid>(alreadyInPlaylist ?? Enumerable.Empty<Guid>());
        BuildShuffledPicks();
        RefreshResults();
    }

    // Debounced. RefreshResults filters the whole library on the UI thread, and
    // MatchesSearch normalizes title/artist/album per track — Take(MaxResults) only
    // short-circuits once enough matches are found, so a narrow query scanned all 50,000
    // tracks per keystroke while the user was still typing. Every other search surface
    // in the app already debounces (Songs/Albums/Artists 250ms, top bar 300ms).
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
        var source = query.Length > 0
            ? _library.Where(t => PlaylistViewModel.MatchesSearch(t, query)).Take(MaxResults)
            : _shuffledPicks;

        foreach (var track in source)
        {
            Results.Add(new AddSongItem(track)
            {
                IsInPlaylist = _alreadyInPlaylist.Contains(track.Id),
                IsSelected = _selected.Contains(track.Id)
            });
        }

        OnPropertyChanged(nameof(IsShuffleMode));
        OnPropertyChanged(nameof(ShowPrompt));
        OnPropertyChanged(nameof(ShowNoResults));
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
    /// Ticks every row currently on screen, or clears them when they are all already
    /// ticked. Scoped to the visible results (not the whole library) so it stays
    /// predictable: what you see is what gets added.
    /// </summary>
    [RelayCommand]
    private void ToggleSelectAll()
    {
        var selectable = SelectableResults.ToList();
        if (selectable.Count == 0) return;

        if (selectable.All(r => r.IsSelected))
        {
            foreach (var item in selectable)
            {
                _selected.Remove(item.Track.Id);
                _selectionOrder.Remove(item.Track.Id);
                item.IsSelected = false;
            }
        }
        else
        {
            // Append in display order, keeping the tick-order contract Add() relies on.
            foreach (var item in selectable.Where(r => !r.IsSelected))
            {
                if (_selected.Add(item.Track.Id))
                    _selectionOrder.Add(item.Track.Id);
                item.IsSelected = true;
            }
        }

        RaiseSelectionChanged();
    }

    private void RaiseSelectionChanged()
    {
        SelectedCount = _selected.Count;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(AddButtonText));
        OnPropertyChanged(nameof(AreAllResultsSelected));
        OnPropertyChanged(nameof(SelectAllText));
    }

    [RelayCommand]
    private void Add()
    {
        if (_selected.Count > 0)
        {
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

    /// <summary>True when the track is already in the target playlist (shown disabled/added).</summary>
    public bool IsInPlaylist { get; set; }

    [ObservableProperty] private bool _isSelected;
}
