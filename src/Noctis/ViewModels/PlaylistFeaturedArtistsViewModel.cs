using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;

namespace Noctis.ViewModels;

public partial class PlaylistFeaturedArtistsViewModel : ViewModelBase, ISearchable
{
    private Action<string>? _viewArtistAction;
    private readonly List<PlaylistFeaturedArtist> _allArtists;

    public PlaylistFeaturedArtistsViewModel(IEnumerable<PlaylistFeaturedArtist> artists)
    {
        _allArtists = artists.ToList();
        Artists = new ObservableCollection<PlaylistFeaturedArtist>(_allArtists);
    }

    public string Title => "Featured Artists";
    public ObservableCollection<PlaylistFeaturedArtist> Artists { get; }

    /// <summary>Filters the artist grid by name via the top-bar search field.</summary>
    public void ApplyFilter(string query)
    {
        var q = query?.Trim() ?? string.Empty;
        Artists.Clear();
        foreach (var artist in _allArtists)
        {
            if (q.Length == 0 || artist.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                Artists.Add(artist);
        }
    }

    public void SetViewArtistAction(Action<string> action) => _viewArtistAction = action;

    [RelayCommand]
    private void OpenArtist(PlaylistFeaturedArtist artist)
    {
        if (!string.IsNullOrWhiteSpace(artist.Name))
            _viewArtistAction?.Invoke(artist.Name);
    }
}
