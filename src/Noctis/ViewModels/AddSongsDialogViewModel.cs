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

    private readonly IReadOnlyList<Track> _library;
    private readonly HashSet<Guid> _alreadyInPlaylist;
    private readonly HashSet<Guid> _selected = new();

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private int _selectedCount;

    public ObservableCollection<AddSongItem> Results { get; } = new();

    public bool ShowPrompt => string.IsNullOrWhiteSpace(SearchText);
    public bool ShowNoResults => !ShowPrompt && Results.Count == 0;
    public bool HasSelection => _selected.Count > 0;
    public string AddButtonText => _selected.Count > 0 ? $"Add {_selected.Count}" : "Add";

    /// <summary>Fires with the chosen tracks when the user confirms.</summary>
    public event EventHandler<IReadOnlyList<Track>>? SongsChosen;

    /// <summary>Fires when the dialog should close.</summary>
    public event EventHandler? CloseRequested;

    public AddSongsDialogViewModel(IReadOnlyList<Track> library, IEnumerable<Guid> alreadyInPlaylist)
    {
        _library = library ?? Array.Empty<Track>();
        _alreadyInPlaylist = new HashSet<Guid>(alreadyInPlaylist ?? Enumerable.Empty<Guid>());
    }

    partial void OnSearchTextChanged(string value) => RefreshResults();

    private void RefreshResults()
    {
        Results.Clear();

        var query = (SearchText ?? string.Empty).Trim();
        if (query.Length > 0)
        {
            foreach (var track in _library
                         .Where(t => PlaylistViewModel.MatchesSearch(t, query))
                         .Take(MaxResults))
            {
                Results.Add(new AddSongItem(track)
                {
                    IsInPlaylist = _alreadyInPlaylist.Contains(track.Id),
                    IsSelected = _selected.Contains(track.Id)
                });
            }
        }

        OnPropertyChanged(nameof(ShowPrompt));
        OnPropertyChanged(nameof(ShowNoResults));
    }

    [RelayCommand]
    private void ToggleSelect(AddSongItem? item)
    {
        if (item == null || item.IsInPlaylist) return;

        if (_selected.Add(item.Track.Id))
            item.IsSelected = true;
        else
        {
            _selected.Remove(item.Track.Id);
            item.IsSelected = false;
        }

        SelectedCount = _selected.Count;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(AddButtonText));
    }

    [RelayCommand]
    private void Add()
    {
        if (_selected.Count > 0)
        {
            var chosen = _library.Where(t => _selected.Contains(t.Id)).ToList();
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
