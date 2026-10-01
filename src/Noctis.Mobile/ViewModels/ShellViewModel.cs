using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Localization;
using Noctis.Mobile.Services;
using Noctis.Mobile.Services.Account;
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
        Player.PlayRecorded += OnPlayRecorded;
        Search = new SearchPageViewModel(this);
        Home = new HomePageViewModel(this);
    }

    public LibraryViewModel Library { get; }
    public NowPlayingViewModel Player { get; }
    public LyricsPageViewModel Lyrics { get; }
    public SearchPageViewModel Search { get; }
    public HomePageViewModel Home { get; }

    /// <summary>Makes the cover tint for album and artist pages; tests inject a synchronous one.</summary>
    public Func<PageTint> TintFactory { get; init; } = () => new PageTint();

    private FavoriteArtistsService? _favoriteArtists;

    /// <summary>Favourite artists, in the desktop's favorite_artists.json (a set of names) under
    /// the data root, so the file means the same thing on both.</summary>
    public FavoriteArtistsService FavoriteArtists
    {
        get => _favoriteArtists ??= new FavoriteArtistsService(Path.Combine(Library.Persistence.DataDirectory, "favorite_artists.json"));
        init => _favoriteArtists = value;
    }

    /// <summary>The system output picker (Android); null in tests and on hosts without one.</summary>
    public IOutputSwitcher? Outputs { get; init; }

    /// <summary>Re-themes the app for Settings (AndroidApp); null in tests unless injected.</summary>
    public IThemeHost? Theme { get; init; }

    /// <summary>Saves the log for Settings → Export logs (Android create-document picker).</summary>
    public ILogExporter? Logs { get; init; }

    /// <summary>"Noctis 1.2.3" for Settings → About; the head reads the package version.</summary>
    public string VersionText { get; init; } = "Noctis";

    /// <summary>The link to the owner's Noctis desktop (sign-in, streaming, downloads, sync).
    /// Null on a host without one and in most tests: Settings → Account and the sheet's
    /// download actions are then hidden.</summary>
    public INoctisAccountService? Account
    {
        get => _account;
        init
        {
            _account = value;
            if (value == null) return;
            value.PlaylistsChanged += OnAccountPlaylistsChanged;
        }
    }
    private readonly INoctisAccountService? _account;

    /// <summary>
    /// A sync writes the desktop's playlists straight into playlists.json and sign-out drops them,
    /// but the Library tab reads that file only at start: reload it each time the service says it
    /// wrote the file, or synced playlists would appear only after a restart. Not on StateChanged:
    /// sign-out raises that before it removes the desktop's playlists, so a reload there read them back.
    /// </summary>
    private void OnAccountPlaylistsChanged(object? sender, EventArgs e) => Marshal(() => _ = Library.ReloadPlaylistsAsync());

    public bool HasAccount => Account != null;

    /// <summary>Hops account events, which arrive on any thread, to the UI thread; tests pass a direct call.</summary>
    public Action<Action> Marshal { get; init; } = a => Avalonia.Threading.Dispatcher.UIThread.Post(a);

    /// <summary>The sync started by <see cref="InitializeAsync"/> when signed in; tests await it.</summary>
    internal Task StartupSync { get; private set; } = Task.CompletedTask;

    /// <summary>Pages pushed over the active tab's root, oldest first. A tab switch clears it.</summary>
    public ObservableCollection<MobilePage> Pages { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMiniBarVisible), nameof(IsMiniBarExpandedVisible), nameof(IsMiniBarInlineVisible))]
    private bool _isNowPlayingOpen;

    [ObservableProperty] private bool _isQueueOpen;
    [ObservableProperty] private bool _isLyricsOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomeSelected), nameof(IsLibrarySelected), nameof(IsSearchSelected), nameof(IsPlaylistsSelected),
        nameof(IsHomeRootVisible), nameof(IsLibraryRootVisible), nameof(IsSearchRootVisible), nameof(IsPlaylistsRootVisible))]
    private MobileTab _selectedTab = MobileTab.Library;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPage), nameof(IsHomeRootVisible), nameof(IsLibraryRootVisible), nameof(IsSearchRootVisible),
        nameof(IsPlaylistsRootVisible))]
    private MobilePage? _currentPage;

    /// <summary>
    /// The tab bar folded into the compact row (the current tab's button, the mini player,
    /// Search), as Apple Music's bar does on scroll down. Set by <see cref="ReportContentScroll"/>;
    /// any tab or page change unfolds it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMiniBarExpandedVisible), nameof(IsMiniBarInlineVisible))]
    private bool _isTabBarCollapsed;

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
    public bool IsPlaylistsSelected => SelectedTab == MobileTab.Playlists;

    public bool HasPage => CurrentPage != null;
    public bool IsHomeRootVisible => IsHomeSelected && !HasPage;
    public bool IsLibraryRootVisible => IsLibrarySelected && !HasPage;
    public bool IsSearchRootVisible => IsSearchSelected && !HasPage;
    public bool IsPlaylistsRootVisible => IsPlaylistsSelected && !HasPage;

    private PlaylistListPageViewModel? _playlistsRoot;

    /// <summary>The Playlists tab's root: the playlist list with Favourite Songs as its first row,
    /// embedded under the tab's own title. Made on first use; it re-reads on every library refresh.</summary>
    public PlaylistListPageViewModel PlaylistsRoot =>
        _playlistsRoot ??= new PlaylistListPageViewModel(this) { IsEmbedded = true };

    // The sides too: in landscape the 3-button navigation bar or a side cutout sits left or
    // right, and without them the content drew under it.
    public Thickness TopSafePadding => new(SafeArea.Left, SafeArea.Top, SafeArea.Right, 0);
    public Thickness BottomSafePadding => new(SafeArea.Left, 0, SafeArea.Right, SafeArea.Bottom);

    /// <summary>The floating pill: whenever a track is loaded and Now Playing is not covering it.</summary>
    public bool IsMiniBarVisible => Player.HasTrack && !IsNowPlayingOpen;

    /// <summary>The mini player as its own capsule above the full tab bar.</summary>
    public bool IsMiniBarExpandedVisible => IsMiniBarVisible && !IsTabBarCollapsed;

    /// <summary>The mini player inside the folded bar, between the tab and Search buttons.</summary>
    public bool IsMiniBarInlineVisible => IsMiniBarVisible && IsTabBarCollapsed;

    /// <summary>Scroll distance in one direction that folds or unfolds the bar, so a finger's
    /// wobble does neither.</summary>
    internal const double TabBarScrollThreshold = 24;

    /// <summary>After a tab or page change, scrolls are the new view settling or ScrollMemory
    /// putting a page back, not the user: they are ignored for this long.</summary>
    internal const long TabBarSettleMs = 400;

    /// <summary>Milliseconds clock; tests drive it.</summary>
    internal Func<long> TickSource { get; set; } = () => Environment.TickCount64;

    private double _scrollRun;
    private long _ignoreScrollUntil;

    /// <summary>
    /// A vertical scroll of the visible tab content: <paramref name="offsetY"/> is the new offset,
    /// <paramref name="deltaY"/> how far it moved (positive = down). Folds the bar after
    /// <see cref="TabBarScrollThreshold"/> of travel down, unfolds it after as much travel up or
    /// on reaching the top.
    /// </summary>
    public void ReportContentScroll(double offsetY, double deltaY)
    {
        if (deltaY == 0) return;
        if (offsetY <= 1)
        {
            _scrollRun = 0;
            IsTabBarCollapsed = false;
            return;
        }
        if (TickSource() < _ignoreScrollUntil) return;
        // A run counts travel in one direction; turning around starts a new one.
        if (Math.Sign(deltaY) != Math.Sign(_scrollRun)) _scrollRun = 0;
        _scrollRun += deltaY;
        if (_scrollRun >= TabBarScrollThreshold) IsTabBarCollapsed = true;
        else if (_scrollRun <= -TabBarScrollThreshold) IsTabBarCollapsed = false;
    }

    private void UnfoldTabBar()
    {
        _scrollRun = 0;
        _ignoreScrollUntil = TickSource() + TabBarSettleMs;
        IsTabBarCollapsed = false;
    }

    [RelayCommand] private void ExpandTabBar() => UnfoldTabBar();

    partial void OnCurrentPageChanged(MobilePage? value) => UnfoldTabBar();

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NowPlayingViewModel.HasTrack) or nameof(NowPlayingViewModel.CurrentTrack))
        {
            OnPropertyChanged(nameof(IsMiniBarVisible));
            OnPropertyChanged(nameof(IsMiniBarExpandedVisible));
            OnPropertyChanged(nameof(IsMiniBarInlineVisible));
        }
    }

    /// <summary>A started track's play reached the log: the Shelf and Home rows re-read it,
    /// and a desktop song's play is queued for the desktop (sent as a scrobble on the next sync).</summary>
    private void OnPlayRecorded(object? sender, EventArgs e)
    {
        RecordRemotePlay();
        Library.RefreshRecents();
        Home.Refresh();
    }

    private void RecordRemotePlay()
    {
        // PlayRecorded fires only while the recorded track is still CurrentTrack.
        if (Account is not { } account || Player.CurrentTrack is not { } track) return;
        try
        {
            if (account.IsRemote(track)) account.RecordPlay(track, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Account", $"Recording a desktop play failed: {ex.Message}");
        }
    }

    partial void OnSelectedTabChanged(MobileTab value)
    {
        if (value == MobileTab.Home) Home.Refresh();
        UnfoldTabBar();
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
        Player.PlayTracks(songs, index, Loc.T("Nav.Songs"));
    }

    [RelayCommand] private void OpenSongs() => Navigate(new SongListPageViewModel(this, Loc.T("Nav.Songs"), () => Library.Songs));

    [RelayCommand] private void OpenFavourites() => Navigate(new SongListPageViewModel(this, Loc.T("Nav.Favorites"), Library.Favourites));

    [RelayCommand] private void OpenRecentlyAdded() => Navigate(new SongListPageViewModel(this, "Recently Added", Library.RecentlyAdded));

    [RelayCommand] private void OpenAlbums() => Navigate(new AlbumGridPageViewModel(this));

    [RelayCommand] private void OpenArtists() => Navigate(new ArtistListPageViewModel(this));

    [RelayCommand] private void OpenPlaylists() => Navigate(new PlaylistListPageViewModel(this));

    // Library sections' › headers.
    [RelayCommand] private void OpenPinned() => Navigate(new RailGridPageViewModel(this, "Pinned", () => Library.PinnedRail));

    [RelayCommand] private void OpenOnRepeat() => Navigate(new SongListPageViewModel(this, "On Repeat", Library.OnRepeatTracks));

    [RelayCommand] private void OpenRecentlyPlayed() => Navigate(new RailGridPageViewModel(this, "Recently Played", () => Library.RecentlyPlayedRail));

    /// <summary>An album tile, row or link: the album page.</summary>
    [RelayCommand]
    private void OpenAlbum(Album? album)
    {
        if (album == null) return;
        Navigate(new AlbumPageViewModel(this, album));
    }

    /// <summary>An artist row or link: the artist page.</summary>
    [RelayCommand]
    private void OpenArtist(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        Navigate(new ArtistPageViewModel(this, name));
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
            case Artist artist:
                OpenArtist(artist.Name);
                break;
            case Track track:
                // A song plays the rail it sits on: On Repeat, else the pinned songs.
                var onRepeat = Library.OnRepeatRail.Select(r => r.Payload).OfType<Track>().ToList();
                var pinned = Library.PinnedRail.Select(r => r.Payload).OfType<Track>().ToList();
                if (onRepeat.IndexOf(track) is var r and >= 0) Player.PlayTracks(onRepeat, r, "On Repeat");
                else if (pinned.IndexOf(track) is var p and >= 0) Player.PlayTracks(pinned, p, "Pinned");
                else Player.PlayTracks(new[] { track }, 0, null);
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
            case Artist artist: Sheet = ContextSheetViewModel.ForArtist(this, artist, item.ArtworkPath); break;
        }
    }

    /// <summary>A long-pressed artist row: play, queue or pin the artist.</summary>
    [RelayCommand]
    private void OpenArtistSheet(ArtistListItem? item)
    {
        if (item != null) Sheet = ContextSheetViewModel.ForArtist(this, item.Artist, item.ArtworkPath);
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

    [RelayCommand]
    private async Task ToggleCurrentFavouriteAsync()
    {
        if (Player.CurrentTrack is { } track) await SetFavouriteAsync(new[] { track }, !track.IsFavorite);
    }

    /// <summary>Now Playing's ⋯: the song's sheet, over the player.</summary>
    [RelayCommand]
    private void OpenCurrentTrackSheet()
    {
        if (Player.CurrentTrack is { } track) Sheet = ContextSheetViewModel.ForTrack(this, track);
    }

    /// <summary>Now Playing's artist line: the artist page (Navigate closes the player).</summary>
    [RelayCommand]
    private void OpenCurrentArtist()
    {
        if (Player.CurrentTrack is { } track) OpenArtist(track.GroupingArtist);
    }

    [RelayCommand]
    private void ShowOutput()
    {
        if (Outputs?.Show() == false) DebugLog.Write("Android", "No output switcher could be shown");
    }

    /// <summary>The profile button (Library and Home, top right).</summary>
    [RelayCommand]
    private void OpenSettings()
    {
        var page = new SettingsPageViewModel(this);
        Navigate(page);
        _ = page.LoadAsync();
    }

    /// <summary>Settings → Account.</summary>
    [RelayCommand]
    private void OpenAccount()
    {
        if (Account != null) Navigate(new AccountPageViewModel(this, Account));
    }

    /// <summary>The sheet's Download: fetches <paramref name="tracks"/> for offline play. Failures
    /// are logged; the Account page shows the running counts.</summary>
    public Task DownloadTracksAsync(IReadOnlyList<Track> tracks) =>
        RunAccountAsync("Download", a => a.DownloadAsync(tracks));

    /// <summary>The sheet's Remove download(s).</summary>
    public Task RemoveDownloadsAsync(IReadOnlyList<Track> tracks) =>
        RunAccountAsync("Remove downloads", a => a.RemoveDownloadsAsync(tracks));

    private async Task RunAccountAsync(string what, Func<INoctisAccountService, Task> work)
    {
        if (Account == null) return;
        try
        {
            await work(Account);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Account", $"{what} failed: {ex.Message}");
        }
    }

    public async Task InitializeAsync()
    {
        await Library.InitializeAsync();
        // Lyrics settings before the queue restore: restoring sets CurrentTrack, which loads
        // that track's lyrics with the saved split-word and layer preferences.
        await Lyrics.InitializeAsync();
        await Player.RestoreStateAsync();
        // Signed in: pull the desktop's changes and push ours. Fire-and-forget over a loaded
        // library; a failure (the computer is off, another network) is logged and never
        // reaches the UI — Settings → Account shows the next manual sync's error instead.
        if (Account is { IsSignedIn: true } account) StartupSync = SyncOnStartAsync(account);
    }

    private static async Task SyncOnStartAsync(INoctisAccountService account)
    {
        try
        {
            if (account.IsSyncing) return;
            var result = await account.SyncNowAsync();
            DebugLog.Write("Account", $"Startup sync: {result.Songs} songs, {result.Playlists} playlists, " +
                $"{result.StateChangesPulled} changes in, {result.StateChangesPushed} out, {result.PlaysSent} plays sent");
        }
        catch (Exception ex)
        {
            DebugLog.Write("Account", $"Startup sync failed: {(ex as NoctisServerException)?.Kind.ToString() ?? ex.GetType().Name}: {ex.Message}");
        }
    }

    public Task SaveStateAsync() => Player.SaveStateAsync();
}
