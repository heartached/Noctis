using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Localization;
using Noctis.Mobile.Services;
using Noctis.Mobile.Services.Account;
using Noctis.Mobile.Services.Scrobbling;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// Root of the phone UI: five tabs, one stack of pages pushed over the active tab's root,
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
        Player.PlayCountThresholdPercent = () => PlayCountThresholdPercent;
        Search = new SearchPageViewModel(this);
    }

    public LibraryViewModel Library { get; }
    public NowPlayingViewModel Player { get; }
    public LyricsPageViewModel Lyrics { get; }
    public SearchPageViewModel Search { get; }

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

    /// <summary>Whether Battery Saver is on (Android); Slow Motion in Settings › Background stands
    /// still while it is. Null in tests unless injected.</summary>
    public IBatterySaver? BatterySaver { get; init; }

    /// <summary>The screen's refresh rate (Android): Settings › Appearance › Smooth Motion asks it
    /// for the fastest one or for 60 Hz. Null in tests unless injected.</summary>
    public IFrameRateHost? FrameRate { get; init; }

    /// <summary>Scrobbling straight from the phone (Settings → Scrobbling): counts plays by the
    /// desktop's rule and sends them to Last.fm / ListenBrainz. The host sets it; Settings makes
    /// one on first use when it is missing (tests), and from then on it follows playback.
    /// </summary>
    public PhoneScrobbler Scrobbler
    {
        get
        {
            if (_scrobbler == null)
            {
                _scrobbler = new PhoneScrobbler(Library.Persistence, new HttpClient());
                AttachScrobbler(_scrobbler);
            }
            return _scrobbler;
        }
        init { _scrobbler = value; AttachScrobbler(value); }
    }
    private PhoneScrobbler? _scrobbler;

    private void AttachScrobbler(PhoneScrobbler scrobbler)
    {
        Player.TrackStarted += (_, track) => scrobbler.TrackStarted(track);
        Player.PlaybackTick += (_, playing) => scrobbler.Tick(playing);
        // A pause the engine reports without a position tick still stops the played-time clock.
        Player.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(NowPlayingViewModel.IsPlaying)) scrobbler.Tick(Player.IsPlaying); };
        // The account recorded the play when it started; whether it counted, and who sends it, follows.
        scrobbler.PlayDecided += (track, verdict) =>
        {
            try { Account?.DecidePlay(track, verdict); }
            catch (Exception ex) { DebugLog.Write("Account", $"Marking a desktop play failed: {ex.Message}"); }
        };
        _ = scrobbler.EnsureLoadedAsync();
    }

    /// <summary>Saves the log for Settings → Export logs (Android create-document picker).</summary>
    public ILogExporter? Logs { get; init; }

    /// <summary>Opens a web page for Settings → Help (Report a Problem, the website); null in tests unless injected.</summary>
    public IUrlLauncher? Links { get; init; }

    /// <summary>Account → Scan QR Code; null (tests, no scanner) hides the button and leaves the
    /// typed sign-in.</summary>
    public IQrScanner? QrScanner { get; init; }

    /// <summary>The album page's description paragraph (Last.fm on Android); null in tests unless
    /// injected, and the paragraph then stays hidden.</summary>
    public IAlbumDescriptionSource? AlbumDescriptions { get; init; }

    /// <summary>The connected Last.fm account's charts for Home's From Your Last.fm and Discover
    /// on Last.fm (GitHub #177); null in tests unless injected, and both sections then stay hidden.</summary>
    public LastFmTasteService? LastFmTaste { get; init; }

    private LastFmHomeViewModel? _lastFm;

    /// <summary>Home's Last.fm sections; made on first use, after the host's init values are set.</summary>
    public LastFmHomeViewModel LastFm => _lastFm ??= new LastFmHomeViewModel(this, LastFmTaste);

    /// <summary>Artist photos (Deezer on Android) for the artist page, the Artists grid, pinned
    /// artists, the artist sheet and search; null in tests unless injected, and the artist's
    /// album cover then stands in everywhere.</summary>
    public IArtistPhotoSource? ArtistPhotos
    {
        get => _artistPhotos;
        init
        {
            _artistPhotos = value;
            Library.ArtistPhotos = value;   // the pinned artists' circles
        }
    }
    private readonly IArtistPhotoSource? _artistPhotos;

    /// <summary>Artists' own logos for the artist page (fanart.tv / TheAudioDB on an Android
    /// build that carries a key); null without one, and the page shows the name.</summary>
    public IArtistLogoSource? ArtistLogos { get; init; }

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
            value.StateChanged += OnAccountStateChanged;
            value.SyncProgress += OnAccountSyncProgress;
            _profileAvatarPath = AccountAvatarPath();
            _downloadedCount = value.DownloadedCount;
            _signedIn = value.IsSignedIn ? 1 : 0;
            Library.Account = value;   // the Library's source choice (On This Phone / From Computer)
            Library.NotifyAccountChanged();
        }
    }
    private readonly INoctisAccountService? _account;

    /// <summary>The signed-in desktop's profile picture for the profile button (ProfileButton);
    /// null when signed out or the desktop has none (the person glyph shows).</summary>
    [ObservableProperty] private string? _profileAvatarPath;

    private string? AccountAvatarPath() => _account is { IsSignedIn: true } a ? a.AvatarPath : null;

    private int _downloadedCount;

    /// <summary>StateChanged fires on every download step too: hop to the UI thread only for a new
    /// picture, or when the offline copies changed (a download batch ended, Remove Download): the
    /// Downloaded list and filter re-read only on a library refresh, and a removed song stayed
    /// listed until the page was reopened.</summary>
    private void OnAccountStateChanged(object? sender, EventArgs e)
    {
        if (AccountAvatarPath() != ProfileAvatarPath) Marshal(() => ProfileAvatarPath = AccountAvatarPath());
        if (_account is not { } account) return;
        var downloaded = account.DownloadedCount;
        if (Interlocked.Exchange(ref _downloadedCount, downloaded) != downloaded) Marshal(Library.NotifyDownloadsChanged);
        var signedIn = account.IsSignedIn ? 1 : 0;
        if (Interlocked.Exchange(ref _signedIn, signedIn) != signedIn)
            Marshal(() =>
            {
                Library.NotifyAccountChanged();
                OnPropertyChanged(nameof(ShowsHomeRemote));
            });
    }

    // Signed in (1) or not, as last seen: the Library's source button shows only signed in (or
    // while the computer's songs are still listed), and StateChanged fires on every download step.
    private int _signedIn;

    /// <summary>
    /// A sync writes the desktop's playlists straight into playlists.json and sign-out drops them,
    /// but the Library tab reads that file only at start: reload it each time the service says it
    /// wrote the file, or synced playlists would appear only after a restart. Not on StateChanged:
    /// sign-out raises that before it removes the desktop's playlists, so a reload there read them back.
    /// </summary>
    private void OnAccountPlaylistsChanged(object? sender, EventArgs e) => Marshal(() => _ = Library.ReloadPlaylistsAsync());

    public bool HasAccount => Account != null;

    /// <summary>The system notification for downloads from the computer (Android); null in tests
    /// unless injected, and the in-app progress still shows.</summary>
    public IDownloadNotifier? DownloadNotifier { get; init; }

    /// <summary>Keeps the process running while downloads run in the background (Android); null in
    /// tests unless injected, and downloads then run only while the process does.</summary>
    public IDownloadKeeper? DownloadKeeper { get; init; }

    /// <summary>Runs the app again for downloads left waiting, even in the background (Android: a
    /// JobScheduler job); null in tests unless injected, and they then wait for the app in front.</summary>
    public IDownloadWakeup? DownloadWakeup { get; init; }

    private DownloadActivity? _downloads;

    /// <summary>Where the downloads from the computer stand (album page row, Account page,
    /// notification); null without an account. Made on first use, after the host's init values.</summary>
    public DownloadActivity? Downloads => _account == null ? null
        : _downloads ??= new DownloadActivity(_account, Marshal, DownloadNotifier, DownloadKeeper, DownloadWakeup)
        {
            IsInFront = () => !IsSuspended,
            ResumeAfterSync = ResumeForWakeupAsync,
        };

    /// <summary>Hops account events, which arrive on any thread, to the UI thread; tests pass a direct call.</summary>
    public Action<Action> Marshal { get; init; } = a => Avalonia.Threading.Dispatcher.UIThread.Post(a);

    /// <summary>The sync started by <see cref="InitializeAsync"/> when signed in; tests await it.</summary>
    internal Task StartupSync { get; private set; } = Task.CompletedTask;

    /// <summary>The start's resume of unfinished downloads (done at once when signed out), set by
    /// <see cref="InitializeAsync"/>: a wakeup run in a process the job started waits for it.</summary>
    private readonly TaskCompletionSource<Task> _startupResume = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Pages pushed over the active tab's root, oldest first. A tab switch clears it.</summary>
    public ObservableCollection<MobilePage> Pages { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMiniBarVisible), nameof(IsMiniBarExpandedVisible), nameof(IsMiniBarInlineVisible),
        nameof(StatusBarIconsDark))]
    private bool _isNowPlayingOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBarIconsDark))]
    private bool _isQueueOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBarIconsDark))]
    private bool _isLyricsOpen;

    /// <summary>The status bar icons the page under the bar asks for (the album cover: dark on a
    /// light cover); null leaves them to the theme.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBarIconsDark))]
    private bool? _pageStatusBarIcons;

    /// <summary>
    /// What the status bar shows: light icons over Now Playing, Lyrics and the Queue (all on a
    /// dark backdrop under the bar, whatever the theme), else the page's choice, else the
    /// theme's (null). Applied to <see cref="Theme"/> on every change.
    /// </summary>
    public bool? StatusBarIconsDark => IsNowPlayingOpen || IsLyricsOpen || IsQueueOpen ? false : PageStatusBarIcons;

    private bool? _appliedStatusBarIcons;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName != nameof(StatusBarIconsDark) || StatusBarIconsDark == _appliedStatusBarIcons) return;
        _appliedStatusBarIcons = StatusBarIconsDark;
        Theme?.SetStatusBarIcons(_appliedStatusBarIcons);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomeSelected), nameof(IsFavoritesSelected), nameof(IsLibrarySelected), nameof(IsSearchSelected),
        nameof(IsPlaylistsSelected), nameof(IsSongsSelected), nameof(IsAlbumsSelected), nameof(IsArtistsSelected), nameof(IsHomeRootVisible), nameof(IsFavoritesRootVisible), nameof(IsLibraryRootVisible),
        nameof(IsSearchRootVisible), nameof(IsPlaylistsRootVisible))]
    private MobileTab _selectedTab = MobileTab.Home;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPage), nameof(IsHomeRootVisible), nameof(IsFavoritesRootVisible), nameof(IsLibraryRootVisible),
        nameof(IsSearchRootVisible), nameof(IsPlaylistsRootVisible))]
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

    /// <summary>
    /// The actions of the song, album, playlist or artist a ⋯ (or a long press) was opened on.
    /// They show as Apple's glass menu popping out of what was tapped (PopMenu); only the Add to
    /// Playlist list, which needs the room, moves to the bottom sheet (ContextSheet) once picked.
    /// Every action closes it through <see cref="CloseSheet"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSheetOpen), nameof(IsPlaylistPickerOpen), nameof(PopMenu), nameof(IsMenuOpen))]
    private ContextSheetViewModel? _sheet;

    /// <summary>An item's actions are up, as the menu or the playlist list.</summary>
    public bool IsSheetOpen => Sheet != null;

    /// <summary>The bottom sheet: only the Add to Playlist list.</summary>
    public bool IsPlaylistPickerOpen => Sheet is { IsPickingPlaylist: true };

    /// <summary>The Library tab's ⋯ menu (Add Folder…, Rescan Library, Settings).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PopMenu), nameof(IsMenuOpen))]
    private LibraryMenuViewModel? _libraryMenu;

    /// <summary>A page's sort or filter menu, or an album's quality note (<see cref="ShowOptions"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PopMenu), nameof(IsMenuOpen))]
    private OptionsMenuViewModel? _optionsMenu;

    /// <summary>Now Playing's artist line tapped: Go to Album and Go to Artist (Apple's bubble).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PopMenu), nameof(IsMenuOpen))]
    private TrackLinksMenuViewModel? _linksMenu;

    /// <summary>Now Playing's quality badge tapped: what the playing file is (<see cref="ShowQualityCard"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PopMenu), nameof(IsMenuOpen))]
    private QualityCardViewModel? _qualityCard;

    /// <summary>What the glass pop menu shows: the Library menu, a sort or filter menu, the
    /// playing song's album and artist links or its quality card, or an item's actions until
    /// its playlist list takes over.</summary>
    public object? PopMenu => (object?)LibraryMenu ?? (object?)OptionsMenu ?? (object?)LinksMenu ?? (object?)QualityCard
        ?? (Sheet is { IsPickingPlaylist: false } sheet ? sheet : null);

    public bool IsMenuOpen => PopMenu != null;

    /// <summary>An album's description, whole, on the glass sheet (<see cref="ShowAlbumNote"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAlbumNoteOpen))]
    private AlbumNoteViewModel? _albumNote;

    public bool IsAlbumNoteOpen => AlbumNote != null;

    /// <summary>An "are you sure?" or a name to type, as Apple's action sheet on the glass
    /// (PromptSheet): Sign Out, Remove Downloads, Remove Folder, Delete Playlist, Rename.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPromptOpen))]
    private PromptViewModel? _prompt;

    public bool IsPromptOpen => Prompt != null;

    /// <summary>Asks <paramref name="prompt"/>, closing whatever menu was up (the one it was
    /// picked in) and any earlier question, which counts as cancelled.</summary>
    public void ShowPrompt(PromptViewModel prompt)
    {
        CloseSheet();
        Prompt = prompt;
    }

    /// <summary>Takes <paramref name="prompt"/> down if it is the one up: cancelled unless one
    /// of its actions ran.</summary>
    public void ClosePrompt(PromptViewModel prompt)
    {
        if (!ReferenceEquals(Prompt, prompt)) return;
        Prompt = null;
        prompt.NotifyCancelled();
    }

    /// <summary>Shows an album's description on the glass sheet, closing whatever menu was up.</summary>
    public void ShowAlbumNote(AlbumNoteViewModel note)
    {
        CloseSheet();
        AlbumNote = note;
    }

    partial void OnSheetChanged(ContextSheetViewModel? oldValue, ContextSheetViewModel? newValue)
    {
        if (oldValue != null) oldValue.PropertyChanged -= OnSheetPropertyChanged;
        if (newValue == null) return;
        newValue.PropertyChanged += OnSheetPropertyChanged;
        AlbumNote = null;
        LibraryMenu = null;
        OptionsMenu = null;
        LinksMenu = null;
        QualityCard = null;
    }

    /// <summary>Add to Playlist picked in the menu: the menu closes and the sheet opens on the list.</summary>
    private void OnSheetPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ContextSheetViewModel.IsPickingPlaylist)) return;
        OnPropertyChanged(nameof(IsPlaylistPickerOpen));
        OnPropertyChanged(nameof(PopMenu));
        OnPropertyChanged(nameof(IsMenuOpen));
    }

    private LibraryRowsViewModel? _libraryRows;

    /// <summary>The Library tab's list rows (Playlists, Artists…) and their Edit mode. Made on
    /// first use, after the host has set <see cref="Account"/> (it decides the Downloaded row).</summary>
    public LibraryRowsViewModel LibraryRows => _libraryRows ??= new LibraryRowsViewModel(this);

    /// <summary>
    /// The UI language changed (Settings › Language, or the saved one applied at start). The
    /// {loc:T} labels re-read on their own; this re-reads what the view models build in code:
    /// the Library rows, the rails, the player's lines and every open page (Settings among them).
    /// </summary>
    public void Relabel()
    {
        foreach (var row in LibraryRows.Rows) row.Relabel();
        Library.Relabel();
        Player.Relabel();
        _lastFm?.Relabel();
        _playlistsRoot?.Relabel();
        _favoritesRoot?.Relabel();
        _songsRoot?.Relabel();
        _albumsRoot?.Relabel();
        _artistsRoot?.Relabel();
        foreach (var page in Pages) page.Relabel();
        OnPropertyChanged(nameof(SongRowMarkLabel)); // Settings › Show in Song Rows kept the old language
        OnPropertyChanged(nameof(QueueHistoryToggleText));
        OnPropertyChanged(nameof(IsRightToLeft));
        OnPropertyChanged(nameof(FlowDirection));
        OnPropertyChanged(nameof(TopSafePadding));
        OnPropertyChanged(nameof(BottomSafePadding));
    }

    public bool IsHomeSelected => SelectedTab == MobileTab.Home;
    public bool IsFavoritesSelected => SelectedTab == MobileTab.Favorites;
    public bool IsLibrarySelected => SelectedTab == MobileTab.Library;
    public bool IsSearchSelected => SelectedTab == MobileTab.Search;
    public bool IsPlaylistsSelected => SelectedTab == MobileTab.Playlists;
    public bool IsSongsSelected => SelectedTab == MobileTab.Songs;
    public bool IsAlbumsSelected => SelectedTab == MobileTab.Albums;
    public bool IsArtistsSelected => SelectedTab == MobileTab.Artists;

    public bool HasPage => CurrentPage != null;
    public bool IsHomeRootVisible => IsHomeSelected && !HasPage;
    public bool IsFavoritesRootVisible => IsFavoritesSelected && !HasPage;
    public bool IsLibraryRootVisible => IsLibrarySelected && !HasPage;
    public bool IsSearchRootVisible => IsSearchSelected && !HasPage;
    public bool IsPlaylistsRootVisible => IsPlaylistsSelected && !HasPage;

    private PlaylistListPageViewModel? _playlistsRoot;
    private SongListPageViewModel? _favoritesRoot;
    private SongListPageViewModel? _songsRoot;
    private AlbumGridPageViewModel? _albumsRoot;
    private ArtistListPageViewModel? _artistsRoot;

    // Every tab's root is made on first use while its tab is on the bar and null otherwise: a root
    // re-reads on every library refresh, so a tab taken off the bar closes and drops its root
    // (OnBarTabsChanged) and a bar without it builds and refreshes none.

    /// <summary>The Playlists tab's root: the playlist list, embedded under the tab's own title.</summary>
    public PlaylistListPageViewModel? PlaylistsRoot => _playlistsRoot ??= HasTab(MobileTab.Playlists)
        ? new PlaylistListPageViewModel(this) { IsEmbedded = true }
        : null;

    /// <summary>The Favorites tab's root: the favourite songs, embedded under the tab's title.</summary>
    public SongListPageViewModel? FavoritesRoot => _favoritesRoot ??= HasTab(MobileTab.Favorites)
        ? new SongListPageViewModel(this, "Nav.Favorites", Library.Favourites, sortKey: "Nav.Favorites") { IsEmbedded = true, OffersFavouritesFilter = false }
        : null;

    /// <summary>The Songs tab's root: every song, embedded under the tab's title (the Library row's page).</summary>
    public SongListPageViewModel? SongsRoot => _songsRoot ??= HasTab(MobileTab.Songs)
        ? new SongListPageViewModel(this, "Nav.Songs", Library.SourceSongs, sortKey: "Nav.Songs") { IsEmbedded = true }
        : null;

    /// <summary>The Albums tab's root: the album grid, embedded under the tab's title.</summary>
    public AlbumGridPageViewModel? AlbumsRoot => _albumsRoot ??= HasTab(MobileTab.Albums)
        ? new AlbumGridPageViewModel(this) { IsEmbedded = true }
        : null;

    /// <summary>The Artists tab's root: the artist circles, embedded under the tab's title.</summary>
    public ArtistListPageViewModel? ArtistsRoot => _artistsRoot ??= HasTab(MobileTab.Artists)
        ? new ArtistListPageViewModel(this) { IsEmbedded = true }
        : null;

    /// <summary>Settings › Tab Bar: the tabs on the bottom bar, in order (TabBarTabs). Set through
    /// <see cref="SetBarTabs"/>; the bar, its pill and the roots follow at once.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartTab), nameof(HasSearchTab))]
    private IReadOnlyList<MobileTab> _barTabs = TabBarTabs.Defaults;

    public bool HasTab(MobileTab tab) => BarTabs.Contains(tab);

    /// <summary>Search is on the bar: the folded bar's round Search button stands only then.</summary>
    public bool HasSearchTab => HasTab(MobileTab.Search);

    /// <summary>Where Back from another tab returns: Home while it is on the bar, else the first tab.</summary>
    public MobileTab StartTab => HasTab(MobileTab.Home) ? MobileTab.Home : BarTabs[0];

    /// <summary>Puts <paramref name="tabs"/> on the bar (normalized: TabBarTabs.Normalize) and saves it.</summary>
    public void SetBarTabs(IEnumerable<MobileTab> tabs)
    {
        var next = TabBarTabs.Normalize(tabs);
        if (next.SequenceEqual(BarTabs)) return;
        BarTabs = next;
    }

    partial void OnBarTabsChanged(IReadOnlyList<MobileTab> value)
    {
        Drop(ref _playlistsRoot, MobileTab.Playlists);
        Drop(ref _favoritesRoot, MobileTab.Favorites);
        Drop(ref _songsRoot, MobileTab.Songs);
        Drop(ref _albumsRoot, MobileTab.Albums);
        Drop(ref _artistsRoot, MobileTab.Artists);
        OnPropertyChanged(nameof(PlaylistsRoot));
        OnPropertyChanged(nameof(FavoritesRoot));
        OnPropertyChanged(nameof(SongsRoot));
        OnPropertyChanged(nameof(AlbumsRoot));
        OnPropertyChanged(nameof(ArtistsRoot));
        // The tab shown left the bar: its root goes, the start tab's comes (the pages pushed over
        // it, and a sheet such as Settings, stay where they are).
        if (!value.Contains(SelectedTab)) SelectedTab = StartTab;
        SavePlayerBarSettings(nameof(BarTabs));

        void Drop<T>(ref T? root, MobileTab tab) where T : MobilePage
        {
            if (root == null || value.Contains(tab)) return;
            root.OnClosed();
            root = null;
        }
    }

    // The sides too: in landscape the 3-button navigation bar or a side cutout sits left or
    // right, and without them the content drew under it. The insets are the screen's own sides:
    // under a right-to-left layout a margin's Left is the screen's right, so they swap.
    public Thickness TopSafePadding => IsRightToLeft
        ? new(SafeArea.Right, SafeArea.Top, SafeArea.Left, 0)
        : new(SafeArea.Left, SafeArea.Top, SafeArea.Right, 0);
    public Thickness BottomSafePadding => IsRightToLeft
        ? new(SafeArea.Right, 0, SafeArea.Left, SafeArea.Bottom)
        : new(SafeArea.Left, 0, SafeArea.Right, SafeArea.Bottom);

    /// <summary>
    /// The phone reads right to left when the strings it shows are a right-to-left language
    /// (Arabic): the setting, or the phone's own language when it ships, as <see cref="Loc.Resolve"/>
    /// picks it. A Hebrew phone shows the English strings, so it stays left to right. The shell
    /// sets it at its root (ShellView), so every page, sheet and menu mirrors with it, live on a
    /// language change (<see cref="Relabel"/>); what must not mirror (media transport, times)
    /// sets LeftToRight itself. The desktop is not touched.
    /// </summary>
    public bool IsRightToLeft => IsRightToLeftLanguage(Loc.Instance.Culture.Name);

    public Avalonia.Media.FlowDirection FlowDirection =>
        IsRightToLeft ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;

    /// <summary>Whether the translation <paramref name="cultureName"/> resolves to is written right to left.</summary>
    internal static bool IsRightToLeftLanguage(string cultureName)
    {
        try { return System.Globalization.CultureInfo.GetCultureInfo(Loc.Resolve(cultureName)).TextInfo.IsRightToLeft; }
        catch (System.Globalization.CultureNotFoundException) { return false; }
    }

    /// <summary>The floating pill: whenever a track is loaded and Now Playing is not covering it;
    /// a song list's Select mode puts its action bar in the pill's place.</summary>
    public bool IsMiniBarVisible => Player.HasTrack && !IsNowPlayingOpen && !IsSelecting;

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
        // Under the Queue the bar stands expanded (OnIsQueueOpenChanged): a list moving in the
        // hidden tab content must not fold it there.
        // Select mode's action bar stands in the unfolded bar's room: it does not fold either.
        if (deltaY == 0 || IsQueueOpen || IsSelecting) return;
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

    partial void OnCurrentPageChanged(MobilePage? value)
    {
        EndActiveSelection();
        UnfoldTabBar();
    }

    /// <summary>The tab bar stays up under the Queue sheet (ShellView), and the folded row has
    /// no place there: opening the Queue unfolds it.</summary>
    partial void OnIsQueueOpenChanged(bool value)
    {
        if (value) UnfoldTabBar();
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPlayerChangedForPickUp(e);
        if (e.PropertyName is nameof(NowPlayingViewModel.HasTrack) or nameof(NowPlayingViewModel.CurrentTrack))
        {
            OnPropertyChanged(nameof(IsMiniBarVisible));
            OnPropertyChanged(nameof(IsMiniBarExpandedVisible));
            OnPropertyChanged(nameof(IsMiniBarInlineVisible));
        }
        if (e.PropertyName is nameof(NowPlayingViewModel.CurrentTrack))
        {
            MiniTint.Load(Player.CurrentTrack?.AlbumArtworkPath);
            UpdateArtworkAccent();
        }
    }

    // ── Settings › Accent › Match Artwork: the accent takes the playing cover's colour ──

    /// <summary>
    /// Match Artwork is on (Settings and the app set it alongside <see cref="IThemeHost.ApplyTheme(string, string, string, bool, bool)"/>).
    /// While on, every playing cover's vibrant colour goes to <see cref="Theme"/>; turned on
    /// mid-song, the current cover's goes at once. Off, no cover is decoded for it.
    /// </summary>
    public bool AccentFollowsArtwork
    {
        get => _accentFollowsArtwork;
        set
        {
            if (_accentFollowsArtwork == value) return;
            _accentFollowsArtwork = value;
            UpdateArtworkAccent();
        }
    }
    private bool _accentFollowsArtwork;

    /// <summary>A cover path to the colour the accent tames, the desktop's "Accent follows album
    /// art" one (<see cref="ArtworkVibrantColor.GetAccentSourceHex"/>: the cover's dominant
    /// colourful region, not its edge); null without readable artwork. Tests inject one.</summary>
    public Func<string, string?> ArtworkAccentSource { get; init; } = ArtworkVibrantColor.GetAccentSourceHex;

    /// <summary>Runs <see cref="ArtworkAccentSource"/> (a Skia decode) off the UI thread; tests run it inline.</summary>
    public Func<Func<string?>, Task<string?>> RunArtworkAccent { get; init; } = work => Task.Run(work);

    /// <summary>The latest Match Artwork colour on its way to the host; tests await it.</summary>
    public Task ArtworkAccentReady { get; private set; } = Task.CompletedTask;

    private int _artworkAccentGeneration;

    private void UpdateArtworkAccent()
    {
        var generation = ++_artworkAccentGeneration;
        ArtworkAccentReady = Task.CompletedTask;
        var path = AccentFollowsArtwork ? Player.CurrentTrack?.AlbumArtworkPath : null;
        // Off, nothing playing or no cover: the host keeps (or goes back to) the chosen accent,
        // and holds no stale cover for the next time Match Artwork is turned on.
        if (string.IsNullOrEmpty(path))
        {
            Theme?.CoverColourChanged(null);
            return;
        }
        ArtworkAccentReady = UpdateArtworkAccentAsync(path, generation);
    }

    private async Task UpdateArtworkAccentAsync(string path, int generation)
    {
        string? hex;
        try
        {
            hex = await RunArtworkAccent(() => ArtworkAccentSource(path));
        }
        catch (Exception ex)
        {
            DebugLog.Write("Artwork", $"Artwork accent failed: {ex.Message}");
            hex = null;
        }
        // A later track (or Match Artwork turned off) won the race: only the newest paints.
        if (generation != _artworkAccentGeneration) return;
        Theme?.CoverColourChanged(hex != null && Avalonia.Media.Color.TryParse(hex, out var c) ? c : null);
    }

    // ── Mini player: the elapsed part of the song fills the capsule in the cover's colour ──

    /// <summary>The playing cover's colour (created on first use: <see cref="TintFactory"/> is an init property).</summary>
    public PageTint MiniTint
    {
        get
        {
            if (_miniTint != null) return _miniTint;
            _miniTint = TintFactory();
            _miniTint.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(PageTint.TintColor))
                {
                    UpdateMiniProgressBrushes();
                    Background.Cover = _miniTint?.TintColor;   // the Now Playing background follows the cover
                }
            };
            return _miniTint;
        }
    }
    private PageTint? _miniTint;

    /// <summary>Settings › Background: the page background, its choice and its saved values.</summary>
    public PhoneBackground Background => _background ??= new PhoneBackground(Library.Persistence, BatterySaver);
    private PhoneBackground? _background;

    /// <summary>The elapsed fill: the cover's colour, soft, so the title stays readable on it.</summary>
    [ObservableProperty] private Avalonia.Media.IBrush _miniProgressFill = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Avalonia.Media.Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));

    /// <summary>The thin line along the fill's bottom edge: the same colour, strong.</summary>
    [ObservableProperty] private Avalonia.Media.IBrush _miniProgressLine = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Avalonia.Media.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));

    private void UpdateMiniProgressBrushes()
    {
        // No cover (or none read yet): a neutral white that works on both glass themes.
        var c = MiniTint.TintColor ?? Avalonia.Media.Colors.White;
        var neutral = MiniTint.TintColor == null;
        // A dark cover's edge colour vanishes on the dark glass: lift it toward white until it reads.
        var luminance = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
        if (luminance < 0.45)
        {
            var t = (0.45 - luminance) / (1 - luminance);
            c = Avalonia.Media.Color.FromRgb((byte)(c.R + (255 - c.R) * t), (byte)(c.G + (255 - c.G) * t), (byte)(c.B + (255 - c.B) * t));
        }
        MiniProgressFill = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Avalonia.Media.Color.FromArgb((byte)(neutral ? 0x26 : 0x59), c.R, c.G, c.B));
        MiniProgressLine = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Avalonia.Media.Color.FromArgb((byte)(neutral ? 0x99 : 0xE6), c.R, c.G, c.B));
    }

    // Settings › Appearance › Player Bar and Album Colours. The player bar's two are off by
    // default: the capsule is plain glass, as Apple's. Saved to settings.json
    // (MobilePlayerBarProgress*, MobileArtworkPageColors) and applied live.

    /// <summary>The elapsed fill in the cover's colour (<see cref="MiniProgressFill"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsMiniProgress))]
    private bool _showMiniProgressFill;

    /// <summary>The bright line along the capsule's bottom edge (<see cref="MiniProgressLine"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsMiniProgress))]
    private bool _showMiniProgressLine;

    /// <summary>Album Colours: album and artist pages painted in a deep shade of the cover's
    /// colour (<see cref="PageTint.Enabled"/>, which the open pages follow); off, they keep the
    /// theme's plain pages. On by default.</summary>
    [ObservableProperty] private bool _artworkPageColors = true;

    /// <summary>Settings › Library › Show in Song Rows: the small mark at the end of a song row's
    /// artist line (Views/RowMark, inherited from ShellView). Ratings by default: the stars show
    /// only on rated songs, so an unrated library looks as before, and the owner's rule of no
    /// heart on the rows (10-19) holds unless Favorites is chosen. Saved to settings.json
    /// (MobileSongRowMark) and applied live.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SongRowMarkLabel))]
    private SongRowMark _songRowMark = SongRowMark.Ratings;

    /// <summary>The Show in Song Rows row's value ("Ratings").</summary>
    public string SongRowMarkLabel => SongRowMarkName(SongRowMark);

    internal static string SongRowMarkName(SongRowMark mark) => Loc.T("Mobile.Settings.SongRows." + mark);

    public bool ShowsMiniProgress => ShowMiniProgressFill || ShowMiniProgressLine;

    /// <summary>Settings › Appearance › Now Playing Artwork: the plain cover (the default), or the
    /// cover on a spinning record or CD (DiscArtwork). Saved with the player bar's settings
    /// (MobileNowPlayingArtwork) and applied live.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsDiscArtwork))]
    private NowPlayingArtworkStyle _nowPlayingArtwork;

    /// <summary>Now Playing draws a disc in place of the square cover.</summary>
    public bool ShowsDiscArtwork => NowPlayingArtwork != NowPlayingArtworkStyle.Cover;

    /// <summary>Beta 2 request: Settings › Appearance › Now Playing Background, -50 (darker) to
    /// 50 (brighter), 0 the stock look. Saved with the player bar's settings and applied live:
    /// brighter thins Now Playing's shade (to 40% of it at 50), darker lays black over it (60%
    /// at -50).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NowPlayingShadeOpacity), nameof(NowPlayingDimOpacity), nameof(NowPlayingBrightnessLabel))]
    private int _nowPlayingBrightness;

    public const int NowPlayingBrightnessLimit = 50;

    /// <summary>The opacity of Now Playing's shade (WashShade): 1 at 0 and darker.</summary>
    public double NowPlayingShadeOpacity => 1 - 0.6 * Math.Max(0, NowPlayingBrightness) / NowPlayingBrightnessLimit;

    /// <summary>The opacity of the black over the shade (WashDim): 0 at 0 and brighter.</summary>
    public double NowPlayingDimOpacity => 0.6 * Math.Max(0, -NowPlayingBrightness) / NowPlayingBrightnessLimit;

    /// <summary>The row's value: "0", "+20", "-35".</summary>
    public string NowPlayingBrightnessLabel => NowPlayingBrightness.ToString("+0;-0;0", System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>Discord #mobile-beta request: Settings › Appearance › Now Playing Background's
    /// style. Wash (the default) is the look above; Blurred Cover paints the cover itself, blurred
    /// (BlurredCoverBackdrop), under a black tint, on Now Playing and its Lyrics. Saved with the
    /// player bar's settings (MobileNowPlayingBackground) and applied live.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBlurredCoverBackground))]
    private NowPlayingBackgroundStyle _nowPlayingBackground;

    public bool IsBlurredCoverBackground => NowPlayingBackground == NowPlayingBackgroundStyle.BlurredCover;

    public const int DefaultNowPlayingBlur = 50, DefaultNowPlayingDim = 15, NowPlayingDimLimit = 60;

    /// <summary>Blurred Cover's Blur, 0-100: the cover's 128 px copy blurred at up to the wash's
    /// own sigma (<see cref="NowPlayingBlurPercent"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NowPlayingBlurPercent), nameof(NowPlayingBlurLabel))]
    private int _nowPlayingBlur = DefaultNowPlayingBlur;

    /// <summary>The Blur as PreBlurredArtworkConverter's percent of its stock sigma: 100 here is
    /// three times it (the wash's 7.2 px at 128 px), 50 one and a half.</summary>
    public int NowPlayingBlurPercent => NowPlayingBlur * 3;

    /// <summary>Blurred Cover's Dim, 0-60: the black over it, in percent. The stock shade stays
    /// under it at its brightest (40%, Brightness +50's), so white text reads on a white cover.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NowPlayingCoverDimOpacity), nameof(NowPlayingDimLabel))]
    private int _nowPlayingDim = DefaultNowPlayingDim;

    public double NowPlayingCoverDimOpacity => NowPlayingDim / 100.0;

    public string NowPlayingBlurLabel => Percent(NowPlayingBlur);
    public string NowPlayingDimLabel => Percent(NowPlayingDim);

    private static string Percent(int value) => (value / 100.0).ToString("P0", System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>The activity is in the background (MainActivity.OnPause: home, task switch or
    /// screen off): Now Playing's disc stops asking for frames until it is back.</summary>
    [ObservableProperty] private bool _isSuspended;

    private bool _playerBarLoaded;
    // Switches flipped before the saved values were read (Settings opens while a cold start still
    // loads the library): the load keeps the user's value and saves it.
    private readonly HashSet<string> _playerBarSetBeforeLoad = new();
    private bool _applyingPlayerBar;
    private Task _playerBarSaveChain = Task.CompletedTask;

    /// <summary>The tail of the player-bar saves; tests await it.</summary>
    internal Task PendingPlayerBarSave { get; private set; } = Task.CompletedTask;

    partial void OnShowMiniProgressFillChanged(bool value) => SavePlayerBarSettings(nameof(ShowMiniProgressFill));
    partial void OnShowMiniProgressLineChanged(bool value) => SavePlayerBarSettings(nameof(ShowMiniProgressLine));
    partial void OnArtworkPageColorsChanged(bool value) => SavePlayerBarSettings(nameof(ArtworkPageColors));
    partial void OnSongRowMarkChanged(SongRowMark value) => SavePlayerBarSettings(nameof(SongRowMark));
    partial void OnNowPlayingArtworkChanged(NowPlayingArtworkStyle value) => SavePlayerBarSettings(nameof(NowPlayingArtwork));
    partial void OnNowPlayingBrightnessChanged(int value) => SavePlayerBarSettings(nameof(NowPlayingBrightness));
    partial void OnNowPlayingBackgroundChanged(NowPlayingBackgroundStyle value) => SavePlayerBarSettings(nameof(NowPlayingBackground));
    partial void OnNowPlayingBlurChanged(int value) => SavePlayerBarSettings(nameof(NowPlayingBlur));
    partial void OnNowPlayingDimChanged(int value) => SavePlayerBarSettings(nameof(NowPlayingDim));

    /// <summary>Settings › Appearance › Liquid Glass Tab Pill (testers 11-06): the selected tab's
    /// pill stays clear liquid glass at rest instead of settling into the dark tinted pill.
    /// Off by default (the glass reads the bar's backdrop every frame it is shown).</summary>
    [ObservableProperty] private bool _glassTabPill;
    partial void OnGlassTabPillChanged(bool value) => SavePlayerBarSettings(nameof(GlassTabPill));

    /// <summary>Settings › Appearance › Reduce Glass (beta testers 11-22: "in some menus it just feels
    /// laggy"): every glass surface (the menus' card, the tab bar, mini player and other floating
    /// bars, the header buttons, the tab pill) is a plain fill that reads nothing beneath it
    /// (GlassPanel.ReduceGlass, set on ShellView). Off by default. While on, Liquid Glass Tab Pill
    /// has nothing to draw and its switch is disabled; its value is kept for when this goes off.</summary>
    [ObservableProperty] private bool _reduceGlass;
    partial void OnReduceGlassChanged(bool value) => SavePlayerBarSettings(nameof(ReduceGlass));

    /// <summary>Settings › Listening › Played Songs in Queue (Discord request 11-07): the Queue page
    /// shows the songs already played above the current track (NowPlayingViewModel.PlayedSongs).
    /// Off by default, the Queue page as it was.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsQueueHistoryRows))]
    private bool _showQueueHistory;
    partial void OnShowQueueHistoryChanged(bool value) => SavePlayerBarSettings(nameof(ShowQueueHistory));

    /// <summary>Settings › Listening › Keep Shuffle On (Discord 11-23, testers: "shuffle turns itself off
    /// when I play something else"): while Shuffle is on, starting a list keeps it on and plays it
    /// shuffled (NowPlayingViewModel.KeepShuffleOn). Off by default; saved under the desktop's key
    /// (AppSettings.KeepShuffleOn).</summary>
    [ObservableProperty] private bool _keepShuffleOn;
    partial void OnKeepShuffleOnChanged(bool value)
    {
        Player.KeepShuffleOn = value;
        SavePlayerBarSettings(nameof(KeepShuffleOn));
    }

    /// <summary>Settings › Account › Show Remote on Home (Discord beta 11-22: "you have to go to the
    /// settings/account page to find it"): a Remote button next to the profile picture on Home.
    /// On by default; saved with the player bar's settings (MobileHomeRemote).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsHomeRemote))]
    private bool _showRemoteOnHome = true;
    partial void OnShowRemoteOnHomeChanged(bool value) => SavePlayerBarSettings(nameof(ShowRemoteOnHome));

    /// <summary>Home's Remote button shows: the switch is on and a computer is signed in (it goes
    /// at sign-out, and comes back at sign-in, without a restart).</summary>
    public bool ShowsHomeRemote => ShowRemoteOnHome && Account is { IsSignedIn: true };

    /// <summary>The Queue page's History is folded to its title (its Hide); for this run of the app only.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsQueueHistoryRows), nameof(QueueHistoryToggleText))]
    private bool _isQueueHistoryCollapsed;

    /// <summary>The History's rows are on screen: the setting is on and the section is not folded.</summary>
    public bool ShowsQueueHistoryRows => ShowQueueHistory && !IsQueueHistoryCollapsed;

    /// <summary>The History title's button: Hide while its rows show, Show once folded.</summary>
    public string QueueHistoryToggleText => IsQueueHistoryCollapsed ? Loc.T("Mobile.Queue.ShowHistory") : Loc.T("Mobile.Queue.HideHistory");

    [RelayCommand] private void ToggleQueueHistoryCollapsed() => IsQueueHistoryCollapsed = !IsQueueHistoryCollapsed;

    private async Task LoadPlayerBarSettingsAsync(AppSettings? saved = null)
    {
        try
        {
            var settings = saved ?? await Library.Persistence.LoadSettingsAsync();
            // A switch flipped while this load ran keeps the user's value: the load used to put
            // the saved one back, and the flip was never saved either.
            _applyingPlayerBar = true;
            try
            {
                if (!_playerBarSetBeforeLoad.Contains(nameof(ShowMiniProgressFill))) ShowMiniProgressFill = settings.MobilePlayerBarProgressFill;
                if (!_playerBarSetBeforeLoad.Contains(nameof(ShowMiniProgressLine))) ShowMiniProgressLine = settings.MobilePlayerBarProgressLine;
                if (!_playerBarSetBeforeLoad.Contains(nameof(ArtworkPageColors))) ArtworkPageColors = settings.MobileArtworkPageColors;
                if (!_playerBarSetBeforeLoad.Contains(nameof(SongRowMark)))
                    SongRowMark = Enum.TryParse<SongRowMark>(settings.MobileSongRowMark, ignoreCase: true, out var mark) && Enum.IsDefined(mark)
                        ? mark : SongRowMark.Ratings;
                if (!_playerBarSetBeforeLoad.Contains(nameof(NowPlayingArtwork))) NowPlayingArtwork = NowPlayingArtworkStyles.Parse(settings.MobileNowPlayingArtwork);
                if (!_playerBarSetBeforeLoad.Contains(nameof(AnimatedCovers))) AnimatedCovers = AnimatedCoversSettings.Parse(settings.MobileAnimatedCovers);
                if (!_playerBarSetBeforeLoad.Contains(nameof(GlassTabPill))) GlassTabPill = settings.MobileGlassTabPill;
                if (!_playerBarSetBeforeLoad.Contains(nameof(ReduceGlass))) ReduceGlass = settings.MobileReduceGlass;
                if (!_playerBarSetBeforeLoad.Contains(nameof(ShowQueueHistory))) ShowQueueHistory = settings.MobileQueueShowHistory;
                if (!_playerBarSetBeforeLoad.Contains(nameof(KeepShuffleOn))) KeepShuffleOn = settings.KeepShuffleOn;
                if (!_playerBarSetBeforeLoad.Contains(nameof(ShowRemoteOnHome))) ShowRemoteOnHome = settings.MobileHomeRemote;
                if (!_playerBarSetBeforeLoad.Contains(nameof(BarTabs))) BarTabs = TabBarTabs.Decode(settings.MobileTabBar);
                if (!_playerBarSetBeforeLoad.Contains(nameof(NowPlayingBrightness)))
                    NowPlayingBrightness = Math.Clamp(settings.MobileNowPlayingBrightness, -NowPlayingBrightnessLimit, NowPlayingBrightnessLimit);
                if (!_playerBarSetBeforeLoad.Contains(nameof(NowPlayingBackground))) NowPlayingBackground = NowPlayingBackgroundStyles.Parse(settings.MobileNowPlayingBackground);
                if (!_playerBarSetBeforeLoad.Contains(nameof(NowPlayingBlur))) NowPlayingBlur = Math.Clamp(settings.MobileNowPlayingBlur, 0, 100);
                if (!_playerBarSetBeforeLoad.Contains(nameof(NowPlayingDim))) NowPlayingDim = Math.Clamp(settings.MobileNowPlayingDim, 0, NowPlayingDimLimit);
            }
            finally
            {
                _applyingPlayerBar = false;
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write("Settings", $"Player bar settings load failed: {ex.Message}");
        }
        _playerBarLoaded = true;
        if (_playerBarSetBeforeLoad.Count > 0) SavePlayerBarSettings();
    }

    /// <summary>One save after another, so a quick on-off never lands out of order.</summary>
    private void SavePlayerBarSettings(string? setting = null)
    {
        if (!_playerBarLoaded)
        {
            if (setting != null && !_applyingPlayerBar) _playerBarSetBeforeLoad.Add(setting);
            return;
        }
        bool fill = ShowMiniProgressFill, line = ShowMiniProgressLine, pages = ArtworkPageColors;
        var mark = SongRowMark.ToString();
        var artwork = NowPlayingArtwork.ToString();
        var animatedCovers = AnimatedCovers.ToString();
        var brightness = NowPlayingBrightness;
        var background = NowPlayingBackground.ToString();
        int blur = NowPlayingBlur, dim = NowPlayingDim;
        var glassPill = GlassTabPill;
        var reduceGlass = ReduceGlass;
        var queueHistory = ShowQueueHistory;
        var keepShuffle = KeepShuffleOn;
        var homeRemote = ShowRemoteOnHome;
        var tabBar = TabBarTabs.Encode(BarTabs);
        PendingPlayerBarSave = _playerBarSaveChain = _playerBarSaveChain.ContinueWith(async _ =>
        {
            try
            {
                // One update: another phone save landing between a plain load and save was undone.
                await Library.Persistence.UpdateSettingsAsync(settings =>
                {
                    settings.MobilePlayerBarProgressFill = fill;
                    settings.MobilePlayerBarProgressLine = line;
                    settings.MobileArtworkPageColors = pages;
                    settings.MobileSongRowMark = mark;
                    settings.MobileNowPlayingArtwork = artwork;
                    settings.MobileAnimatedCovers = animatedCovers;
                    settings.MobileNowPlayingBrightness = brightness;
                    settings.MobileNowPlayingBackground = background;
                    settings.MobileNowPlayingBlur = blur;
                    settings.MobileNowPlayingDim = dim;
                    settings.MobileGlassTabPill = glassPill;
                    settings.MobileReduceGlass = reduceGlass;
                    settings.MobileQueueShowHistory = queueHistory;
                    settings.KeepShuffleOn = keepShuffle;
                    settings.MobileHomeRemote = homeRemote;
                    settings.MobileTabBar = tabBar;
                });
            }
            catch (Exception ex)
            {
                DebugLog.Write("Settings", $"Player bar settings save failed: {ex.Message}");
            }
        }, TaskScheduler.Default).Unwrap();
    }

    /// <summary>A started track's play reached the log: the Library's play-log rails re-read it,
    /// and a desktop song's play is queued for the desktop (sent as a scrobble on the next sync).</summary>
    private void OnPlayRecorded(object? sender, EventArgs e)
    {
        RecordRemotePlay();
        Library.RefreshRecents();
    }

    private void RecordRemotePlay()
    {
        // PlayRecorded fires only while the recorded track is still CurrentTrack. Share with
        // Computer off keeps the play on the phone.
        if (!ShareHistoryWithComputer) return;
        if (Account is not { } account || Player.CurrentTrack is not { } track) return;
        try
        {
            if (account.DesktopIdOf(track) is null) return;
            // With the scrobbler following playback, the play waits for its verdict (Through
            // Computer then has the computer send it on, once, only if it counted). Without one
            // nothing would ever decide it: it is sent as a plain count.
            if (_scrobbler != null) account.RecordStartedPlay(track, DateTime.UtcNow);
            else account.RecordPlay(track, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Account", $"Recording a desktop play failed: {ex.Message}");
        }
    }

    partial void OnSelectedTabChanged(MobileTab value)
    {
        EndActiveSelection();
        UnfoldTabBar();
    }

    /// <summary>How the last change of <see cref="CurrentPage"/> came about, raised before it:
    /// ShellView moves the pages to match (a push slides in, a pop slides out, a reset jumps).</summary>
    public PageMove LastPageMove { get; private set; }

    private bool _resetting;

    /// <summary>Push <paramref name="page"/> over the current tab (inside the sheet, when one is
    /// up). A page opened from Now Playing (the artist link), Lyrics or the Queue closes them
    /// first: Lyrics opens over a list without the player too, and the page landed under it.</summary>
    public void Navigate(MobilePage page) => Push(page, sheet: false);

    /// <summary>Present <paramref name="page"/> as a sheet over everything (Settings); Back or its
    /// close button drops it, and pages pushed from it open inside it.</summary>
    public void Present(MobilePage page) => Push(page, sheet: !Pages.Any(p => p.IsSheet));

    private void Push(MobilePage page, bool sheet)
    {
        CloseSheet();
        if (IsPlayerUp) CloseNowPlaying();
        page.IsSheet = sheet;
        page.PushedOver = Pages.Count > 0 ? Pages[^1] : null;
        page.PushedOnTab = SelectedTab;
        Pages.Add(page);
        LastPageMove = PageMove.Push;
        CurrentPage = page;
    }

    /// <summary>Pop the top page. False when the tab root is already showing.</summary>
    public bool GoBack()
    {
        if (Pages.Count == 0) return false;
        var top = Pages[^1];
        Pages.RemoveAt(Pages.Count - 1);
        top.OnClosed();
        LastPageMove = _resetting ? PageMove.Reset : PageMove.Pop;
        CurrentPage = Pages.Count > 0 ? Pages[^1] : null;
        return true;
    }

    /// <summary>Drops the sheet (Settings, with Account if it is open inside it): a swipe down on
    /// it. False when no sheet is up.</summary>
    public bool DismissSheet()
    {
        if (!Pages.Any(p => p.IsSheet)) return false;
        while (Pages.Any(p => p.IsSheet)) GoBack();
        return true;
    }

    /// <summary>Back to the tab's root at once (a tab tap): no page slides on the way.</summary>
    public void PopToRoot()
    {
        _resetting = true;
        try
        {
            while (GoBack()) { }
        }
        finally
        {
            _resetting = false;
        }
    }

    [RelayCommand] private void NavigateBack() => GoBack();

    /// <summary>A tab tap always lands on that tab's root, so re-tapping the current tab pops to it.
    /// The bar stays up under the Queue, and a tap there closes the Queue and Now Playing too.</summary>
    [RelayCommand]
    private void SelectTab(MobileTab tab)
    {
        CloseNowPlaying();
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
        CloseSheet();
        IsNowPlayingOpen = false;
    }

    [RelayCommand] private void ToggleQueue() => IsQueueOpen = !IsQueueOpen;

    [RelayCommand] private void ToggleLyrics() => IsLyricsOpen = !IsLyricsOpen;

    /// <summary>
    /// Android Back: the topmost thing closes first — a ⋯ menu or its sheet, Queue, Lyrics, Now
    /// Playing, then pushed pages (a page's own inner page first), then the Library rows' Edit
    /// mode finishes (as Done would), then a non-start tab returns to the start tab (Home, or the
    /// bar's first tab without it: <see cref="StartTab"/>). Returns whether the press was consumed;
    /// only at the start tab's root does the activity fall through to the system default (finish).
    /// </summary>
    public bool TryHandleBack()
    {
        if (IsSheetOpen || IsMenuOpen || IsAlbumNoteOpen || IsPromptOpen) { CloseSheet(); return true; }
        if (IsQueueOpen) { IsQueueOpen = false; return true; }
        if (IsLyricsOpen) { IsLyricsOpen = false; return true; }
        if (IsNowPlayingOpen) { IsNowPlayingOpen = false; return true; }
        // A song list's Select mode ends before its page goes (as Done would).
        if (ActiveSelection is { } selection) { selection.End(); return true; }
        if (CurrentPage?.TryGoBackWithin() == true) return true;
        if (GoBack()) return true;
        if (_libraryRows is { IsEditing: true } rows) { rows.FinishEditing(); return true; }
        if (SelectedTab != StartTab) { SelectedTab = StartTab; return true; }
        return false;
    }

    /// <summary>How long after a Back key the activity's BackRequested counts as that same press.</summary>
    internal const long BackKeyEchoMs = 1000;

    private long? _backKeyHandledAt;

    /// <summary>
    /// Back that arrived as an Escape KeyDown (3-button navigation, a hardware Back key). On the
    /// S23 the same press then also raises the activity's BackRequested, Escape handled or not,
    /// so one press closed two layers (Lyrics and Now Playing). <see cref="TryHandleSystemBack"/>
    /// skips that echo.
    /// </summary>
    public bool TryHandleBackKey()
    {
        if (!TryHandleBack()) return false;
        _backKeyHandledAt = TickSource();
        return true;
    }

    /// <summary>
    /// Back from the activity (gesture navigation, or the echo of a Back key that
    /// <see cref="TryHandleBackKey"/> already acted on, which is consumed without acting again).
    /// </summary>
    public bool TryHandleSystemBack()
    {
        if (_backKeyHandledAt is { } at)
        {
            _backKeyHandledAt = null;
            if (TickSource() - at < BackKeyEchoMs) return true;
        }
        return TryHandleBack();
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

    [RelayCommand] private void OpenSongs() => Navigate(new SongListPageViewModel(this, "Nav.Songs", Library.SourceSongs, sortKey: "Nav.Songs"));

    [RelayCommand] private void OpenFavourites() => Navigate(new SongListPageViewModel(this, "Nav.Favorites", Library.Favourites, sortKey: "Nav.Favorites") { OffersFavouritesFilter = false });

    [RelayCommand] private void OpenRecentlyAdded() => Navigate(new SongListPageViewModel(this, "Mobile.RecentlyAdded", Library.RecentlyAdded, sortKey: "Mobile.RecentlyAdded"));

    [RelayCommand] private void OpenAlbums() => Navigate(new AlbumGridPageViewModel(this));

    [RelayCommand] private void OpenArtists() => Navigate(new ArtistListPageViewModel(this));

    [RelayCommand] private void OpenPlaylists() => Navigate(new PlaylistListPageViewModel(this));

    // Library sections' › headers.
    [RelayCommand] private void OpenPinned() => Navigate(new RailGridPageViewModel(this, "Mobile.Pinned", () => Library.PinnedRail));

    [RelayCommand] private void OpenOnRepeat() => Navigate(new SongListPageViewModel(this, "Mobile.OnRepeat", Library.OnRepeatTracks, sortKey: "Mobile.OnRepeat"));

    /// <summary>Home › Most Played: every played song, in the list's Most Played sort until the
    /// user picks another (then that one, as any list's).</summary>
    [RelayCommand]
    private void OpenMostPlayed()
    {
        var page = new SongListPageViewModel(this, "Home.MostPlayed", Library.MostPlayedTracks, sortKey: "Home.MostPlayed");
        if (!TryGetListSort<SongSort>("Home.MostPlayed", out _, out _))
            page.ChooseSort(page.SortChoices.First(c => c.Key is SongSort.MostPlayed));
        Navigate(page);
    }

    [RelayCommand] private void OpenRecentlyPlayed() => Navigate(new RailGridPageViewModel(this, "Mobile.RecentlyPlayed", () => Library.RecentlyPlayedRail));

    /// <summary>Home › Top Artists: the rail's artists as a grid.</summary>
    [RelayCommand] private void OpenTopArtists() => Navigate(new RailGridPageViewModel(this, "Home.TopArtists", () => Library.TopArtistsRail));

    /// <summary>Home › Downloaded: every album with a song kept on this phone.</summary>
    [RelayCommand] private void OpenDownloaded() => Navigate(new RailGridPageViewModel(this, "Mobile.Downloaded", Library.DownloadedAlbums));

    /// <summary>An album tile, row or link: the album page.</summary>
    [RelayCommand]
    private void OpenAlbum(Album? album)
    {
        if (album == null) return;
        if (IsShowingAlbum(album.Id))
        {
            ShowCurrentPage();
            return;
        }
        Navigate(new AlbumPageViewModel(this, CurrentAlbum(album)));
    }

    /// <summary>An artist row or link: the artist page.</summary>
    [RelayCommand]
    private void OpenArtist(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        if (IsShowingArtist(name))
        {
            ShowCurrentPage();
            return;
        }
        Navigate(new ArtistPageViewModel(this, name));
    }

    // Go to Album / Go to Artist from that album's or artist's own page (Now Playing over it, a
    // track's menu on it) pushed the same page again, and Back then showed it twice.
    public bool IsShowingAlbum(Guid albumId) => CurrentPage is AlbumPageViewModel page && page.Album.Id == albumId;

    public bool IsShowingArtist(string name) =>
        CurrentPage is ArtistPageViewModel page && string.Equals(page.Name, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether Now Playing, Lyrics or the Queue covers the pages.</summary>
    public bool IsPlayerUp => IsNowPlayingOpen || IsLyricsOpen || IsQueueOpen;

    /// <summary>The page asked for is the one under the menus and the player: uncover it.</summary>
    private void ShowCurrentPage()
    {
        CloseSheet();
        if (IsPlayerUp) CloseNowPlaying();
    }

    [RelayCommand]
    private void OpenPlaylist(Playlist? playlist)
    {
        if (playlist == null) return;
        Navigate(new SongListPageViewModel(this, playlist.Name, () => ResolvePlaylist(CurrentPlaylist(playlist)), playlist));
    }

    /// <summary>Every playlist edit (and a sync) re-reads playlists.json into new objects: a page
    /// or menu holding one from before reads the playlist the library holds now, by id.</summary>
    public Playlist CurrentPlaylist(Playlist playlist) =>
        Library.Playlists.FirstOrDefault(p => p.Id == playlist.Id) ?? playlist;

    // ── Playlist editing (desktop parity: SidebarViewModel / PlaylistViewModel) ──

    /// <summary>Rename: the glass prompt with the name in a pill field, selected to be typed
    /// over. An empty name cannot be saved; the same name saves nothing (the desktop's rule).</summary>
    public void AskRenamePlaylist(Playlist playlist)
    {
        var current = CurrentPlaylist(playlist);
        ShowPrompt(new PromptViewModel(this, Loc.T("Mobile.RenamePlaylist"), string.Empty,
            new[] { new PromptAction(Loc.T("Mobile.Save"), p => RenamePlaylistAsync(current, p.Entered)) })
        {
            HasField = true,
            FieldPlaceholder = Loc.T("Mobile.PlaylistName"),
            FieldText = current.Name,
        });
    }

    /// <summary>The Playlists page's New Playlist (+): the Rename prompt's name field, empty; the
    /// playlist is made as a song's "Add to Playlist › New playlist" makes one, with no songs yet.</summary>
    [RelayCommand]
    private void AskNewPlaylist() =>
        ShowPrompt(new PromptViewModel(this, Loc.T("Mobile.Playlists.New"), string.Empty,
            new[] { new PromptAction(Loc.T("Mobile.Create"), p => CreatePlaylistAsync(p.Entered)) })
        {
            HasField = true,
            FieldPlaceholder = Loc.T("Mobile.PlaylistName"),
        });

    public async Task CreatePlaylistAsync(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return;
        await Library.CreatePlaylistAsync(name);
    }

    public async Task RenamePlaylistAsync(Playlist playlist, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || string.Equals(name, CurrentPlaylist(playlist).Name, StringComparison.Ordinal)) return;
        await Library.RenamePlaylistAsync(playlist, name);
    }

    /// <summary>Delete Playlist asks first, as the desktop does ("This cannot be undone"); one of
    /// the computer's says it goes there too.</summary>
    public void AskDeletePlaylist(Playlist playlist)
    {
        var current = CurrentPlaylist(playlist);
        var synced = Account?.IsSyncedPlaylist(current.Id) == true;
        ShowPrompt(new PromptViewModel(this, Loc.T("Mobile.DeletePlaylistQuestion", current.Name),
            synced ? Loc.T("Mobile.DeletePlaylistSynced") : Loc.T("Mobile.DeletePlaylistLocal"),
            new[] { new PromptAction(Loc.T("Mobile.DeletePlaylist"), _ => DeletePlaylistAsync(current), isDestructive: true) }));
    }

    /// <summary>Deletes the playlist (and its page, if it is the one on top); one of the
    /// computer's is queued to be deleted there on the next sync.</summary>
    public async Task DeletePlaylistAsync(Playlist playlist)
    {
        try
        {
            Account?.RecordPlaylistDeleted(playlist.Id);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Account", $"Recording a playlist delete failed: {ex.Message}");
        }
        if (CurrentPage is SongListPageViewModel { Playlist: { } shown } && shown.Id == playlist.Id) GoBack();
        await Library.DeletePlaylistAsync(playlist);
    }

    /// <summary>Change Cover: a picture from the phone's picker becomes the playlist's cover (one of
    /// the computer's takes it there too on the next sync). A file that is not a picture says so.</summary>
    public async Task ChangePlaylistCoverAsync(Playlist playlist)
    {
        if (PlaylistFiles is not { } picker) return;
        PickedPlaylistFile? image;
        try
        {
            image = await picker.PickImageAsync();
        }
        catch (Exception ex)
        {
            DebugLog.Write("Library", $"Picking a playlist cover failed: {ex.Message}");
            return;
        }
        if (image is null) return;
        if (!await Library.SetPlaylistCoverAsync(CurrentPlaylist(playlist), image.Content))
            Player.Notice = Loc.T("Mobile.CoverNotPicture");
    }

    /// <summary>Remove Cover: back to the first song's cover, on the computer too for one of its playlists.</summary>
    public async Task RemovePlaylistCoverAsync(Playlist playlist)
    {
        try
        {
            Account?.RecordPlaylistCoverRemoved(playlist.Id);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Account", $"Recording a cover removal failed: {ex.Message}");
        }
        await Library.RemovePlaylistCoverAsync(CurrentPlaylist(playlist));
    }

    /// <summary>Add Songs (a playlist's page and its menu): the library's songs on a sheet, ticked
    /// ones added at Done. Only for a playlist the phone edits (not a smart one).</summary>
    public void OpenAddSongs(Playlist playlist)
    {
        var current = CurrentPlaylist(playlist);
        if (!AddSongsPageViewModel.CanAddTo(current) || Pages.Any(p => p is AddSongsPageViewModel)) return;
        Present(new AddSongsPageViewModel(this, current));
    }

    /// <summary>A song's Remove from Playlist (no question asked, as on the desktop and Apple's).</summary>
    public async Task RemoveFromPlaylistAsync(Playlist playlist, IReadOnlyList<Track> tracks)
    {
        await Library.RemoveFromPlaylistAsync(CurrentPlaylist(playlist), tracks);
    }

    /// <summary>A playlist's tracks in saved order; ids no longer in the library are skipped.</summary>
    public IEnumerable<Track> ResolvePlaylist(Playlist playlist) =>
        playlist.TrackIds.Select(Library.Service.GetTrackById).OfType<Track>();

    /// <summary>A rail tile: albums and playlists open, an On Repeat song plays that rail from it.</summary>
    [RelayCommand]
    private void OpenRailItem(RailItem? item)
    {
        switch (CurrentPayload(item))
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
            case LastFmMix mix:
                // From Your Last.fm: the mix's songs as a list, in its order, as the library holds them now.
                Navigate(new SongListPageViewModel(this, mix.TitleKey, () => mix.TrackIds.Select(Library.Service.GetTrackById).OfType<Track>()));
                break;
            case Track track:
                // A song plays the rail it sits on: Most Played (its own tile: the same song can sit
                // on On Repeat too), On Repeat, else the pinned songs.
                if (Library.MostPlayedRail.FirstOrDefault(i => ReferenceEquals(i, item)) != null)
                {
                    var mostPlayed = Library.MostPlayedRail.Select(CurrentPayload).OfType<Track>().ToList();
                    Player.PlayTracks(mostPlayed, Math.Max(0, mostPlayed.IndexOf(track)), Loc.T("Home.MostPlayed"));
                    break;
                }
                var onRepeat = Library.OnRepeatRail.Select(CurrentPayload).OfType<Track>().ToList();
                var pinned = Library.PinnedRail.Select(CurrentPayload).OfType<Track>().ToList();
                if (onRepeat.IndexOf(track) is var r and >= 0) Player.PlayTracks(onRepeat, r, Loc.T("Mobile.OnRepeat"));
                else if (pinned.IndexOf(track) is var p and >= 0) Player.PlayTracks(pinned, p, Loc.T("Mobile.Pinned"));
                else Player.PlayTracks(new[] { track }, 0, null);
                break;
        }
    }

    [RelayCommand]
    private void OpenTrackSheet(Track? track)
    {
        if (track != null) Sheet = ContextSheetViewModel.ForTrack(this, track);
    }

    /// <summary>A song's menu on a list; <paramref name="inPlaylist"/> is the playlist whose page
    /// it is on, which adds Remove from Playlist.</summary>
    public void OpenTrackMenu(Track track, Playlist? inPlaylist, TrackSelection? selection = null) =>
        Sheet = ContextSheetViewModel.ForTrack(this, track, inPlaylist, selection);

    [RelayCommand]
    private void OpenAlbumSheet(Album? album)
    {
        if (album != null) Sheet = ContextSheetViewModel.ForAlbum(this, CurrentAlbum(album));
    }

    /// <summary>The album page's ⋯: the album's menu, with Select for its track list.</summary>
    public void OpenAlbumMenu(Album album, TrackSelection selection) =>
        Sheet = ContextSheetViewModel.ForAlbum(this, CurrentAlbum(album), selection);

    [RelayCommand]
    private void OpenPlaylistSheet(Playlist? playlist)
    {
        if (playlist != null) Sheet = ContextSheetViewModel.ForPlaylist(this, CurrentPlaylist(playlist));
    }

    [RelayCommand]
    private void OpenRailItemSheet(RailItem? item)
    {
        switch (CurrentPayload(item))
        {
            case Album album: OpenAlbumSheet(album); break;
            case Playlist playlist: OpenPlaylistSheet(playlist); break;
            case Track track: OpenTrackSheet(track); break;
            case Artist artist: Sheet = ContextSheetViewModel.ForArtist(this, artist, item!.ArtworkPath); break;
        }
    }

    /// <summary>Album grids and rails keep unchanged tiles across library refreshes, so a tile's
    /// album can be an object from before a rescan: open the one the library holds now.</summary>
    private Album CurrentAlbum(Album album) => Library.Service.GetAlbumById(album.Id) ?? album;

    /// <summary>A rail keeps an unchanged tile across library refreshes (<see cref="RailItem.Equals(RailItem?)"/>),
    /// so its payload can be an object from before a rescan: act on what the library holds now.</summary>
    private object? CurrentPayload(RailItem? item) => item?.Payload switch
    {
        Track track => Library.Service.GetTrackById(track.Id) ?? track,
        Artist artist => Library.Service.Artists.FirstOrDefault(a => string.Equals(a.Name, artist.Name, StringComparison.OrdinalIgnoreCase)) ?? artist,
        var other => other,
    };

    /// <summary>A long-pressed artist row: play, queue or pin the artist.</summary>
    [RelayCommand]
    private void OpenArtistSheet(ArtistListItem? item)
    {
        if (item != null) Sheet = ContextSheetViewModel.ForArtist(this, item.Artist, item.ArtworkPath);
    }

    private readonly Noctis.Helpers.BulkObservableCollection<Playlist> _pickerPlaylists = new();
    private string[] _pickerNames = Array.Empty<string>();

    /// <summary>
    /// Add to Playlist's list (the manual playlists), one collection kept from sheet to sheet:
    /// a new list on every open made the sheet build a row for every playlist each time. It is
    /// replaced only when the playlists or their names changed (a rename is made in place).
    /// </summary>
    internal IReadOnlyList<Playlist> PickerPlaylists()
    {
        var next = Library.Playlists.Where(p => !p.IsSmartPlaylist).ToList();
        var names = next.Select(p => p.Name).ToArray();
        var same = next.Count == _pickerPlaylists.Count && names.SequenceEqual(_pickerNames)
            && next.Zip(_pickerPlaylists).All(pair => ReferenceEquals(pair.First, pair.Second));
        if (!same)
        {
            _pickerPlaylists.ReplaceAll(next);
            _pickerNames = names;
        }
        return _pickerPlaylists;
    }

    /// <summary>Closes whichever menu or sheet is open.</summary>
    [RelayCommand]
    public void CloseSheet()
    {
        if (Prompt is { } prompt) ClosePrompt(prompt);
        AlbumNote = null;
        Sheet = null;
        LibraryMenu = null;
        OptionsMenu = null;
        LinksMenu = null;
        QualityCard = null;
    }

    /// <summary>Opens a sort or filter menu (or a note) on the glass pop menu, out of the button
    /// tapped, closing whatever menu was up.</summary>
    public void ShowOptions(OptionsMenuViewModel menu)
    {
        AlbumNote = null;
        Sheet = null;
        LibraryMenu = null;
        LinksMenu = null;
        QualityCard = null;
        OptionsMenu = menu;
    }

    /// <summary>
    /// Favourite or unfavourite <paramref name="tracks"/>: the flag, one journal write through
    /// the Core user-state path (not a library.json rewrite), then FavoritesChanged so the
    /// Library counts, open lists and album hearts follow.
    /// </summary>
    public async Task SetFavouriteAsync(IReadOnlyList<Track> tracks, bool favourite)
    {
        if (tracks.Count == 0) return;
        // The library's own songs too: the queue keeps its Track objects, and a rescan that
        // re-read a changed file (or a sync) made the library a new one. The save journals the
        // library's copy, so flagging only the queue's lost the heart (never in Favorites).
        var current = tracks.Select(t => Library.Service.GetTrackById(t.Id) ?? t).ToList();
        foreach (var t in tracks.Concat(current)) t.IsFavorite = favourite;
        try
        {
            await Library.Service.SaveTrackUserStateAsync(current);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Library", $"Favourite save failed: {ex.Message}");
        }
        Library.Service.NotifyFavoritesChanged(current);
    }

    /// <summary>
    /// Rate <paramref name="tracks"/> 0 (none) to 5 stars, the favourite's path: the library's own
    /// songs too, one journal write (which queues a desktop song's rating for the computer on the
    /// next sync, NoctisAccountService.RecordTrackStates). Track.Rating notifies, so the rows'
    /// stars follow at once.
    /// </summary>
    public async Task SetRatingAsync(IReadOnlyList<Track> tracks, int rating)
    {
        if (tracks.Count == 0) return;
        rating = Math.Clamp(rating, 0, 5);
        var current = tracks.Select(t => Library.Service.GetTrackById(t.Id) ?? t).ToList();
        foreach (var t in tracks.Concat(current)) t.Rating = rating;
        try
        {
            await Library.Service.SaveTrackUserStateAsync(current);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Library", $"Rating save failed: {ex.Message}");
        }
    }

    /// <summary>The desktop's Snooze for a Month (PlayerViewModel.SnoozeDurationDays).</summary>
    public const int SnoozeDays = 30;

    /// <summary>
    /// Snooze <paramref name="tracks"/> for a month, or end their snooze: the desktop's
    /// SetTracksSnoozedAsync (app state only, no tag write), on the library's own songs and the
    /// queue's copies. Shuffle then holds them out (PlaybackQueue.IsHeldOutOfShuffle) and the
    /// Last.fm mixes are built again without them; a desktop song's snooze goes to the computer
    /// on the next sync (NoctisAccountService.RecordTrackStates).
    /// </summary>
    public async Task SetSnoozedAsync(IReadOnlyList<Track> tracks, bool snooze)
    {
        if (tracks.Count == 0) return;
        var current = tracks.Select(t => Library.Service.GetTrackById(t.Id) ?? t).DistinctBy(t => t.Id).ToList();
        DateTime? until = snooze ? DateTime.UtcNow.AddDays(SnoozeDays) : null;
        try
        {
            await Library.Service.SetTracksSnoozedAsync(current, until);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Library", $"Snooze save failed: {ex.Message}");
        }
        // After the save: a copy that is the library's own song must still differ when it runs.
        foreach (var t in tracks) t.SnoozedUntil = until;
        _lastFm?.SuggestionsChanged();
        Player.Notice = Loc.T(snooze ? "Mobile.SnoozedNotice" : "Mobile.UnsnoozedNotice");
    }

    /// <summary>
    /// Hide Song (Discord #mobile-beta: voice notes and other audio that is not music): the
    /// desktop's hidden-folder list (AppSettings.HiddenLibraryFolders, LibraryService.SetFolderHiddenAsync)
    /// with the song's own path in it, which hides that one file (FolderPathMatch.IsInOrSameAsAny).
    /// The library rebuilds without it, so Songs, Albums, Artists, search, Shuffle and the mixes
    /// leave it out; Settings › Hidden shows it again. Nothing is deleted or rescanned.
    /// </summary>
    public async Task SetHiddenAsync(IReadOnlyList<string> paths, bool hidden)
    {
        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal))
        {
            try
            {
                await Library.Service.SetFolderHiddenAsync(path, hidden);
            }
            catch (Exception ex)
            {
                DebugLog.Write("Library", $"Hide failed: {ex.Message}");
            }
        }
        if (hidden) Player.Notice = Loc.T("Mobile.HiddenNotice");
    }

    [RelayCommand]
    private async Task ToggleCurrentFavouriteAsync()
    {
        if (Player.CurrentTrack is { } track) await SetFavouriteAsync(new[] { track }, !track.IsFavorite);
    }

    /// <summary>Now Playing's ⋯: the song's glass menu, over the player.</summary>
    [RelayCommand]
    private void OpenNowPlayingMenu()
    {
        if (Player.CurrentTrack is { } track) Sheet = ContextSheetViewModel.ForTrack(this, track);
    }

    /// <summary>The Sleep Timer's choices on the glass menu: from the playing song's ⋯ menu, or
    /// out of Now Playing's timer pill while one runs.</summary>
    [RelayCommand]
    private void ShowSleepTimerMenu() => ShowOptions(OptionsMenuViewModel.ForSleepTimer(this, Player));

    /// <summary>The Library tab's source button: All Music, On This Phone or From Computer on the
    /// glass menu, for the Songs, Albums, Artists and Recently Added lists.</summary>
    [RelayCommand]
    private void ShowLibrarySourceMenu() => ShowOptions(OptionsMenuViewModel.ForLibrarySource(this, Library));

    /// <summary>The Library tab's ⋯: Add Folder…, Rescan Library and Settings on the glass menu.</summary>
    [RelayCommand]
    private void OpenLibraryMenu()
    {
        Sheet = null;
        OptionsMenu = null;
        LinksMenu = null;
        QualityCard = null;
        LibraryMenu = new LibraryMenuViewModel(this);
    }

    /// <summary>The Queue's ⋯: the song's menu, over the Queue.</summary>
    [RelayCommand]
    private void OpenCurrentTrackSheet()
    {
        if (Player.CurrentTrack is { } track) Sheet = ContextSheetViewModel.ForTrack(this, track);
    }

    /// <summary>Now Playing's artist line (Apple's): a glass bubble out of the artist with Go to
    /// Album and Go to Artist, each opening its page (Navigate closes the player).</summary>
    [RelayCommand]
    private void OpenCurrentLinks()
    {
        if (Player.CurrentTrack is not { } track) return;
        Sheet = null;
        LibraryMenu = null;
        OptionsMenu = null;
        QualityCard = null;
        LinksMenu = new TrackLinksMenuViewModel(this, track);
    }

    /// <summary>Now Playing's quality badge: what the playing file is, on a glass card out of the
    /// badge (<see cref="QualityCardViewModel"/>). Nothing without a badge to tap.</summary>
    [RelayCommand]
    private void ShowQualityCard()
    {
        if (Player.Quality is not { } quality) return;
        Sheet = null;
        LibraryMenu = null;
        OptionsMenu = null;
        LinksMenu = null;
        QualityCard = new QualityCardViewModel(this, quality);
    }

    [RelayCommand]
    private void ShowOutput()
    {
        if (Outputs?.Show() == false) DebugLog.Write("Android", "No output switcher could be shown");
    }

    /// <summary>The profile button (top right of the tab pages).</summary>
    [RelayCommand]
    private void OpenSettings()
    {
        // A second tap while the sheet rises is the same request, not a second sheet.
        if (Pages.Any(p => p is SettingsPageViewModel)) return;
        var page = new SettingsPageViewModel(this);
        Present(page);
        _ = page.LoadAsync();
    }

    /// <summary>Settings → Account, pushed inside the Settings sheet.</summary>
    [RelayCommand]
    private void OpenAccount()
    {
        if (Account != null && CurrentPage is not AccountPageViewModel) Navigate(new AccountPageViewModel(this, Account));
    }

    /// <summary>Account › Remote: the computer's own player, pushed inside the Settings sheet (or
    /// over Home, from its Remote button: <see cref="ShowsHomeRemote"/>).</summary>
    [RelayCommand]
    private void OpenComputerRemote()
    {
        if (Account is { IsSignedIn: true } && CurrentPage is not ComputerRemotePageViewModel)
            Navigate(new ComputerRemotePageViewModel(this, Account));
    }

    /// <summary>The sheet's Download: fetches <paramref name="tracks"/> for offline play, as one start
    /// named <paramref name="title"/> (the album page, the Account page and the notification follow
    /// it). The banner says it started; a refusal the user can act on (Wi-Fi only, phone full, signed
    /// out) replaces it, other failures are logged.</summary>
    public async Task DownloadTracksAsync(IReadOnlyList<Track> tracks, string? title = null)
    {
        if (Account is not { } account || Downloads is not { } downloads) return;
        Player.Notice = Loc.T("Mobile.Account.StartingDownloads");
        try
        {
            await downloads.RunAsync(title, tracks, () => account.DownloadAsync(tracks));
        }
        catch (NoctisServerException ex)
        {
            DebugLog.Write("Account", $"Download failed: {ex.Kind}");
            // Before this, a tap on mobile data with Wi-Fi only on did nothing that could be seen.
            Player.Notice = AccountPageViewModel.Describe(ex.Kind, signedIn: true);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Account", $"Download failed: {ex.Message}");
        }
    }

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

    /// <param name="settings">The settings the host read and applied at start (AndroidApp.StartAsync):
    /// every part below takes its values from them instead of reading settings.json once more each
    /// (it was read eight times per cold launch). Null = read here, once. A value changed since that
    /// read is kept all the same: each part keeps what the user set before its load.</param>
    public Task InitializeAsync() => InitializeAsync(null);

    public async Task InitializeAsync(AppSettings? settings)
    {
        if (settings == null)
        {
            try { settings = await Library.Persistence.LoadSettingsAsync(); }
            catch (Exception ex) { DebugLog.Write("Settings", $"Start settings load failed: {ex.Message}"); }   // each part reads its own
        }
        await Library.InitializeAsync(settings);
        await LoadPlayerBarSettingsAsync(settings);
        await LoadListSortsAsync(settings);
        await Search.LoadRecentSearchesAsync(settings);
        await Background.LoadAsync(settings);
        await LoadHistoryPrefsAsync(settings);
        // Lyrics settings before the queue restore: restoring sets CurrentTrack, which loads
        // that track's lyrics with the saved split-word and layer preferences.
        await Lyrics.InitializeAsync(settings);
        // ReplayGain before the queue restore too: the restored song's level is set as it loads.
        await Player.LoadReplayGainAsync(settings);
        try
        {
            await Player.RestoreStateAsync();
        }
        finally
        {
            _queueRestored = true;
        }
        // Signed in: pull the desktop's changes and push ours. Fire-and-forget over a loaded
        // library; a failure (the computer is off, another network) is logged and never
        // reaches the UI — Settings → Account shows the next manual sync's error instead.
        if (Account is { IsSignedIn: true } account)
        {
            var sync = SyncOnStartAsync(account);
            StartupSync = sync;
            _startupResume.TrySetResult(ResumeUnfinishedDownloadsAsync(sync));
        }
        else
        {
            _startupResume.TrySetResult(Task.CompletedTask);
            // Signed out: a run left scheduled has nothing to resume.
            DownloadWakeup?.Cancel();
        }
        // Home's Last.fm sections (cached for 12 h: normally no request).
        _ = LastFm.RefreshAsync(force: false);
    }

    /// <returns>Whether the computer answered (a sync already running counts as yes).</returns>
    private static async Task<bool> SyncOnStartAsync(INoctisAccountService account)
    {
        try
        {
            if (account.IsSyncing) return true;
            var result = await account.SyncNowAsync();
            DebugLog.Write("Account", $"Startup sync: {result.Songs} songs, {result.Playlists} playlists, " +
                $"{result.StateChangesPulled} changes in, {result.StateChangesPushed} out, {result.PlaysSent} plays sent");
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Write("Account", $"Startup sync failed: {(ex as NoctisServerException)?.Kind.ToString() ?? ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Downloads the app ended in the middle of go on by themselves (DownloadActivity.ResumeUnfinishedAsync),
    /// after the startup sync: long after the first frame, and with the catalog saying which songs the
    /// computer still has. A computer that did not answer would fail every song: they wait for the
    /// next start instead.
    /// </summary>
    private async Task ResumeUnfinishedDownloadsAsync(Task<bool> sync)
    {
        try
        {
            if (!await sync || Downloads is not { } downloads) return;
            await downloads.ResumeUnfinishedAsync();
        }
        catch (Exception ex)
        {
            DebugLog.Write("Account", $"Resuming downloads failed: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// The wakeup ran with nothing downloading (DownloadActivity): in a process the job itself started,
    /// the start's own sync and resume (the app is not on screen, so the downloads it resumes run under
    /// the job); later, a sync now, so the catalog says which songs the computer still has, then the
    /// songs still asked for. A computer that does not answer resumes nothing, as at start.
    /// </summary>
    private async Task ResumeForWakeupAsync()
    {
        var startup = await _startupResume.Task;
        if (!startup.IsCompleted)
        {
            await startup;
            return;
        }
        if (Account is { IsSignedIn: true } account) await ResumeUnfinishedDownloadsAsync(SyncOnStartAsync(account));
    }

    /// <summary>Back in front: downloads waiting for the app (or for Wi-Fi) are looked at again.</summary>
    partial void OnIsSuspendedChanged(bool value)
    {
        if (!value && _downloads is { } downloads) _ = downloads.ResumeWaitingAsync();
    }

    // Set once the saved queue has been read back (InitializeAsync).
    private bool _queueRestored;

    /// <summary>
    /// The activity's background checkpoint (MainActivity.OnPause). Not before the saved queue
    /// has been restored: the activity pauses mid-start (Android 13's notification prompt, or a
    /// switch away while the library loads), and saving then wrote the empty, not yet restored
    /// queue over queue.json, so the restore found nothing and the mini player never came back.
    /// A queue the owner changes before the restore is saved by the player's own mutators.
    /// </summary>
    public Task SaveStateAsync() => _queueRestored ? Player.SaveStateAsync() : Task.CompletedTask;
}
