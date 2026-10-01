using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Models;

namespace Noctis.Mobile.ViewModels;

/// <summary>The Search tab's root. Re-runs on every keystroke and after every library refresh.</summary>
public sealed partial class SearchPageViewModel : ObservableObject
{
    internal const int SongCap = 25;
    internal const int AlbumCap = 12;
    internal const int ArtistCap = 12;

    public SearchPageViewModel(ShellViewModel shell)
    {
        Shell = shell;
        shell.Library.Refreshed += (_, _) => Run();
    }

    public ShellViewModel Shell { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQuery), nameof(ShowPrompt))]
    private string _query = string.Empty;

    public BulkObservableCollection<Track> Songs { get; } = new();
    public BulkObservableCollection<Album> Albums { get; } = new();
    public BulkObservableCollection<ArtistListItem> Artists { get; } = new();

    [ObservableProperty] private bool _hasSongs;
    [ObservableProperty] private bool _hasAlbums;
    [ObservableProperty] private bool _hasArtists;

    public bool HasQuery => !string.IsNullOrWhiteSpace(Query);
    public bool ShowPrompt => !HasQuery;
    public bool ShowNoResults => HasQuery && !HasSongs && !HasAlbums && !HasArtists;

    partial void OnQueryChanged(string value) => Run();

    internal void Run()
    {
        var results = MobileSearch.Find(Shell.Library.Service, Query, SongCap, AlbumCap, ArtistCap);
        Songs.ReplaceAll(results.Songs);
        Albums.ReplaceAll(results.Albums);
        Artists.ReplaceAll(results.Artists);
        HasSongs = Songs.Count > 0;
        HasAlbums = Albums.Count > 0;
        HasArtists = Artists.Count > 0;
        OnPropertyChanged(nameof(ShowNoResults));
    }

    /// <summary>A song result plays the song results from it.</summary>
    [RelayCommand]
    private void PlaySong(Track? track)
    {
        if (track == null) return;
        var list = Songs.ToList();
        var index = list.IndexOf(track);
        if (index >= 0) Shell.Player.PlayTracks(list, index, "Search");
    }

    [RelayCommand]
    private void OpenArtist(ArtistListItem? item)
    {
        if (item != null) Shell.OpenArtistCommand.Execute(item.Name);
    }
}
