using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// Root of the phone UI: three tabs, one stack of pages pushed over the active tab's root,
/// the Now Playing / Lyrics / Queue overlays and the mini bar. Every Android Back decision
/// is made here (<see cref="TryHandleBack"/>) so it is testable on Windows.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    public ShellViewModel(LibraryViewModel library, NowPlayingViewModel player, LyricsPageViewModel lyrics)
    {
        Library = library;
        Player = player;
        Lyrics = lyrics;
        Player.PropertyChanged += OnPlayerChanged;
    }

    public LibraryViewModel Library { get; }
    public NowPlayingViewModel Player { get; }
    public LyricsPageViewModel Lyrics { get; }

    /// <summary>Makes the cover tint for album and artist pages; tests inject a synchronous one.</summary>
    public Func<PageTint> TintFactory { get; init; } = () => new PageTint();

    /// <summary>Pages pushed over the active tab's root, oldest first. A tab switch clears it.</summary>
    public ObservableCollection<MobilePage> Pages { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMiniBarVisible))]
    private bool _isNowPlayingOpen;

    [ObservableProperty] private bool _isQueueOpen;
    [ObservableProperty] private bool _isLyricsOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomeSelected), nameof(IsLibrarySelected), nameof(IsSearchSelected),
        nameof(IsHomeRootVisible), nameof(IsLibraryRootVisible), nameof(IsSearchRootVisible))]
    private MobileTab _selectedTab = MobileTab.Library;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPage), nameof(IsHomeRootVisible), nameof(IsLibraryRootVisible), nameof(IsSearchRootVisible))]
    private MobilePage? _currentPage;

    /// <summary>
    /// System-bar insets (status bar on top, navigation/gesture bar at the bottom) from the
    /// Android insets manager; zero in headless tests. ShellView keeps it current.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TopSafePadding), nameof(BottomSafePadding))]
    private Thickness _safeArea;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllMusicChip), nameof(IsPlaylistsChip), nameof(IsAlbumsChip), nameof(IsArtistsChip), nameof(IsSongsChip))]
    private LibraryChip _libraryChip = LibraryChip.AllMusic;

    /// <summary>The list shown under a non-"All Music" chip: the same page a tile pushes, embedded.</summary>
    [ObservableProperty] private MobilePage? _libraryChipPage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSheetOpen))]
    private ContextSheetViewModel? _sheet;

    public bool IsSheetOpen => Sheet != null;

    public bool IsAllMusicChip => LibraryChip == LibraryChip.AllMusic;
    public bool IsPlaylistsChip => LibraryChip == LibraryChip.Playlists;
    public bool IsAlbumsChip => LibraryChip == LibraryChip.Albums;
    public bool IsArtistsChip => LibraryChip == LibraryChip.Artists;
    public bool IsSongsChip => LibraryChip == LibraryChip.Songs;

    public bool IsHomeSelected => SelectedTab == MobileTab.Home;
    public bool IsLibrarySelected => SelectedTab == MobileTab.Library;
    public bool IsSearchSelected => SelectedTab == MobileTab.Search;

    public bool HasPage => CurrentPage != null;
    public bool IsHomeRootVisible => IsHomeSelected && !HasPage;
    public bool IsLibraryRootVisible => IsLibrarySelected && !HasPage;
    public bool IsSearchRootVisible => IsSearchSelected && !HasPage;

    public Thickness TopSafePadding => new(0, SafeArea.Top, 0, 0);
    public Thickness BottomSafePadding => new(0, 0, 0, SafeArea.Bottom);

    /// <summary>The floating pill: whenever a track is loaded and Now Playing is not covering it.</summary>
    public bool IsMiniBarVisible => Player.HasTrack && !IsNowPlayingOpen;

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NowPlayingViewModel.HasTrack) or nameof(NowPlayingViewModel.CurrentTrack))
            OnPropertyChanged(nameof(IsMiniBarVisible));
        if (e.PropertyName == nameof(NowPlayingViewModel.CurrentTrack))
            Library.RefreshRecents();
    }

    /// <summary>Push <paramref name="page"/> over the current tab. A page opened from Now
    /// Playing (the artist link) closes the player first.</summary>
    public void Navigate(MobilePage page)
    {
        Sheet = null;
        if (IsNowPlayingOpen) CloseNowPlaying();
        Pages.Add(page);
        CurrentPage = page;
    }

    /// <summary>Pop the top page. False when the tab root is already showing.</summary>
    public bool GoBack()
    {
        if (Pages.Count == 0) return false;
        var top = Pages[^1];
        Pages.RemoveAt(Pages.Count - 1);
        top.OnClosed();
        CurrentPage = Pages.Count > 0 ? Pages[^1] : null;
        return true;
    }

    public void PopToRoot()
    {
        while (GoBack()) { }
    }

    [RelayCommand] private void NavigateBack() => GoBack();

    /// <summary>A tab tap always lands on that tab's root, so re-tapping the current tab pops to it.</summary>
    [RelayCommand]
    private void SelectTab(MobileTab tab)
    {
        PopToRoot();
        SelectedTab = tab;
    }

    [RelayCommand] private void OpenNowPlaying() => IsNowPlayingOpen = true;

    [RelayCommand]
    private void CloseNowPlaying()
    {
        // The Queue sits above Now Playing, so closing the page must take it down too;
        // otherwise the Queue overlay is left floating over the Library page.
        IsQueueOpen = false;
        IsLyricsOpen = false;
        IsNowPlayingOpen = false;
    }

    [RelayCommand] private void ToggleQueue() => IsQueueOpen = !IsQueueOpen;

    [RelayCommand] private void ToggleLyrics() => IsLyricsOpen = !IsLyricsOpen;

    /// <summary>
    /// Android Back: the topmost thing closes first — the long-press sheet, Queue, Lyrics, Now
    /// Playing, then pushed pages, then a non-start tab returns to Library. A Library chip
    /// other than All Music then returns to All Music. Returns whether the press was consumed;
    /// only at the All Music root does the activity fall through to the system default (finish).
    /// </summary>
    public bool TryHandleBack()
    {
        if (IsSheetOpen) { CloseSheet(); return true; }
        if (IsQueueOpen) { IsQueueOpen = false; return true; }
        if (IsLyricsOpen) { IsLyricsOpen = false; return true; }
        if (IsNowPlayingOpen) { IsNowPlayingOpen = false; return true; }
        if (GoBack()) return true;
        if (SelectedTab != MobileTab.Library) { SelectedTab = MobileTab.Library; return true; }
        // A chip list (Songs, Albums…) reads as a page of its own: Back returns to All Music
        // rather than leaving the app from it.
        if (LibraryChip != LibraryChip.AllMusic) { SelectLibraryChip(LibraryChip.AllMusic); return true; }
        return false;
    }

    /// <summary>Tap on a song row: play the song list from that row.</summary>
    [RelayCommand]
    private void PlaySong(Track? track)
    {
        if (track == null) return;
        var songs = Library.Songs.ToList();
        var index = songs.IndexOf(track);
        if (index < 0) return;
        Player.PlayTracks(songs, index);
    }

    [RelayCommand] private void OpenSongs() => Navigate(new SongListPageViewModel(this, Loc.T("Nav.Songs"), () => Library.Songs));

    [RelayCommand] private void OpenFavourites() => Navigate(new SongListPageViewModel(this, Loc.T("Nav.Favorites"), Library.Favourites));

    [RelayCommand] private void OpenRecentlyAdded() => Navigate(new SongListPageViewModel(this, "Recently Added", Library.RecentlyAdded));

    [RelayCommand] private void OpenAlbums() => Navigate(new AlbumGridPageViewModel(this));

    [RelayCommand] private void OpenArtists() => Navigate(new ArtistListPageViewModel(this));

    [RelayCommand] private void OpenPlaylists() => Navigate(new PlaylistListPageViewModel(this));

    /// <summary>An album tile, row or link: the album page.</summary>
    [RelayCommand]
    private void OpenAlbum(Album? album)
    {
        if (album == null) return;
        Navigate(new AlbumPageViewModel(this, album));
    }

    /// <summary>An artist row or link. Until the artist page (B6) this lists the artist's songs.</summary>
    [RelayCommand]
    private void OpenArtist(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        Navigate(new SongListPageViewModel(this, name, () => MobileLibrary.SongsBy(Library.Service, name)));
    }

    [RelayCommand]
    private void OpenPlaylist(Playlist? playlist)
    {
        if (playlist == null) return;
        Navigate(new SongListPageViewModel(this, playlist.Name, () => ResolvePlaylist(playlist)));
    }

    /// <summary>A playlist's tracks in saved order; ids no longer in the library are skipped.</summary>
    public IEnumerable<Track> ResolvePlaylist(Playlist playlist) =>
        playlist.TrackIds.Select(Library.Service.GetTrackById).OfType<Track>();

    [RelayCommand]
    private void SelectLibraryChip(LibraryChip chip)
    {
        if (chip == LibraryChip) return;
        LibraryChipPage?.OnClosed();
        LibraryChip = chip;
        LibraryChipPage = chip switch
        {
            LibraryChip.Playlists => new PlaylistListPageViewModel(this) { IsEmbedded = true },
            LibraryChip.Albums => new AlbumGridPageViewModel(this) { IsEmbedded = true },
            LibraryChip.Artists => new ArtistListPageViewModel(this) { IsEmbedded = true },
            LibraryChip.Songs => new SongListPageViewModel(this, Loc.T("Nav.Songs"), () => Library.Songs) { IsEmbedded = true },
            _ => null,
        };
    }

    /// <summary>A rail tile: albums and playlists open, an On Repeat song plays that rail from it.</summary>
    [RelayCommand]
    private void OpenRailItem(RailItem? item)
    {
        switch (item?.Payload)
        {
            case Album album:
                OpenAlbum(album);
                break;
            case Playlist playlist:
                OpenPlaylist(playlist);
                break;
            case Track track:
                var rail = Library.OnRepeatRail.Select(r => r.Payload).OfType<Track>().ToList();
                var index = rail.IndexOf(track);
                if (index >= 0) Player.PlayTracks(rail, index);
                else Player.PlayTracks(new[] { track }, 0);
                break;
        }
    }

    [RelayCommand]
    private void OpenTrackSheet(Track? track)
    {
        if (track != null) Sheet = ContextSheetViewModel.ForTrack(this, track);
    }

    [RelayCommand]
    private void OpenAlbumSheet(Album? album)
    {
        if (album != null) Sheet = ContextSheetViewModel.ForAlbum(this, album);
    }

    [RelayCommand]
    private void OpenPlaylistSheet(Playlist? playlist)
    {
        if (playlist != null) Sheet = ContextSheetViewModel.ForPlaylist(this, playlist);
    }

    [RelayCommand]
    private void OpenRailItemSheet(RailItem? item)
    {
        switch (item?.Payload)
        {
            case Album album: OpenAlbumSheet(album); break;
            case Playlist playlist: OpenPlaylistSheet(playlist); break;
            case Track track: OpenTrackSheet(track); break;
        }
    }

    [RelayCommand]
    public void CloseSheet() => Sheet = null;

    /// <summary>
    /// Favourite or unfavourite <paramref name="tracks"/>: the flag, one journal write through
    /// the Core user-state path (not a library.json rewrite), then FavoritesChanged so the
    /// Library counts, open lists and album hearts follow.
    /// </summary>
    public async Task SetFavouriteAsync(IReadOnlyList<Track> tracks, bool favourite)
    {
        if (tracks.Count == 0) return;
        foreach (var t in tracks) t.IsFavorite = favourite;
        try
        {
            await Library.Service.SaveTrackUserStateAsync(tracks.ToList());
        }
        catch (Exception ex)
        {
            DebugLog.Write("Library", $"Favourite save failed: {ex.Message}");
        }
        Library.Service.NotifyFavoritesChanged(tracks.ToList());
    }

    public async Task InitializeAsync()
    {
        await Library.InitializeAsync();
        // Lyrics settings before the queue restore: restoring sets CurrentTrack, which loads
        // that track's lyrics with the saved split-word and layer preferences.
        await Lyrics.InitializeAsync();
        await Player.RestoreStateAsync();
    }

    public Task SaveStateAsync() => Player.SaveStateAsync();
}
