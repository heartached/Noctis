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
            _accountWasSignedIn = value.IsSignedIn;
            _accountLastSync = value.Account?.LastSyncUtc;
            value.StateChanged += OnAccountStateChanged;
        }
    }
    private readonly INoctisAccountService? _account;
    private DateTime? _accountLastSync;
    private bool _accountWasSignedIn;

    /// <summary>
    /// A sync writes the desktop's playlists straight into playlists.json and sign-out drops them,
    /// but the Library tab reads that file only at start: reload it when a sync ends or the
    /// account signs out, or synced playlists would appear only after a restart. Keyed on
    /// LastSyncUtc, not IsSyncing: the handler reads state when it runs, so a quick sync can
    /// be over before any hop observes it running.
    /// </summary>
    private void OnAccountStateChanged(object? sender, EventArgs e) => Marshal(() =>
    {
        if (_account is not { } account) return;
        var lastSync = account.Account?.LastSyncUtc;
        var signedIn = account.IsSignedIn;
        var reload = (lastSync != null && lastSync != _accountLastSync) || (_accountWasSignedIn && !signedIn);
        _accountLastSync = lastSync;
        _accountWasSignedIn = signedIn;
        if (reload) _ = Library.ReloadPlaylistsAsync();
    });

    public bool HasAccount => Account != null;

    /// <summary>Hops account events, which arrive on any thread, to the UI thread; tests pass a direct call.</summary>
    public Action<Action> Marshal { get; init; } = a => Avalonia.Threading.Dispatcher.UIThread.Post(a);

    /// <summary>The sync started by <see cref="InitializeAsync"/> when signed in; tests await it.</summary>
    internal Task StartupSync { get; private set; } = Task.CompletedTask;

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

    // The sides too: in landscape the 3-button navigation bar or a side cutout sits left or
    // right, and without them the content drew under it.
    public Thickness TopSafePadding => new(SafeArea.Left, SafeArea.Top, SafeArea.Right, 0);
    public Thickness BottomSafePadding => new(SafeArea.Left, 0, SafeArea.Right, SafeArea.Bottom);

    /// <summary>The floating pill: whenever a track is loaded and Now Playing is not covering it.</summary>
    public bool IsMiniBarVisible => Player.HasTrack && !IsNowPlayingOpen;

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NowPlayingViewModel.HasTrack) or nameof(NowPlayingViewModel.CurrentTrack))
            OnPropertyChanged(nameof(IsMiniBarVisible));
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
