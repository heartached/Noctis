using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;

namespace Noctis.ViewModels;

public partial class PlaylistFeaturedArtistsViewModel : ViewModelBase
{
    private Action<string>? _viewArtistAction;

    public PlaylistFeaturedArtistsViewModel(IEnumerable<PlaylistFeaturedArtist> artists)
    {
        Artists = new ObservableCollection<PlaylistFeaturedArtist>(artists);
    }

    public string Title => "Featured Artists";
    public ObservableCollection<PlaylistFeaturedArtist> Artists { get; }

    public void SetViewArtistAction(Action<string> action) => _viewArtistAction = action;

    [RelayCommand]
    private void OpenArtist(PlaylistFeaturedArtist artist)
    {
        if (!string.IsNullOrWhiteSpace(artist.Name))
            _viewArtistAction?.Invoke(artist.Name);
    }
}
