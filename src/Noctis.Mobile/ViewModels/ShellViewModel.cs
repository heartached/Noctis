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
    }

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
        nameof(IsPlaylistsSelected), nameof(IsHomeRootVisible), nameof(IsFavoritesRootVisible), nameof(IsLibraryRootVisible),
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
        foreach (var page in Pages) page.Relabel();
    }

    public bool IsHomeSelected => SelectedTab == MobileTab.Home;
    public bool IsFavoritesSelected => SelectedTab == MobileTab.Favorites;
    public bool IsLibrarySelected => SelectedTab == MobileTab.Library;
    public bool IsSearchSelected => SelectedTab == MobileTab.Search;
    public bool IsPlaylistsSelected => SelectedTab == MobileTab.Playlists;

    public bool HasPage => CurrentPage != null;
    public bool IsHomeRootVisible => IsHomeSelected && !HasPage;
    public bool IsFavoritesRootVisible => IsFavoritesSelected && !HasPage;
    public bool IsLibraryRootVisible => IsLibrarySelected && !HasPage;
    public bool IsSearchRootVisible => IsSearchSelected && !HasPage;
    public bool IsPlaylistsRootVisible => IsPlaylistsSelected && !HasPage;

    private PlaylistListPageViewModel? _playlistsRoot;
    private SongListPageViewModel? _favoritesRoot;

    /// <summary>The Playlists tab's root: the playlist list, embedded under the tab's own title.
    /// Made on first use; it re-reads on every library refresh.</summary>
    public PlaylistListPageViewModel PlaylistsRoot =>
        _playlistsRoot ??= new PlaylistListPageViewModel(this) { IsEmbedded = true };

    /// <summary>The Favorites tab's root: the favourite songs, embedded under the tab's title.</summary>
    public SongListPageViewModel FavoritesRoot =>
        _favoritesRoot ??= new SongListPageViewModel(this, "Nav.Favorites", Library.Favourites) { IsEmbedded = true, OffersFavouritesFilter = false };

    // The sides too: in landscape the 3-button navigation bar or a side cutout sits left or
    // right, and without them the content drew under it.
    public Thickness TopSafePadding => new(SafeArea.Left, SafeArea.Top, SafeArea.Right, 0);
    public Thickness BottomSafePadding => new(SafeArea.Left, 0, SafeArea.Right, SafeArea.Bottom);

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
            MiniTint.Load(Player.CurrentTrack?.AlbumArtworkPath);
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

    private async Task LoadPlayerBarSettingsAsync()
    {
        try
        {
            var settings = await Library.Persistence.LoadSettingsAsync();
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
    /// mode finishes (as Done would), then a non-start tab returns to Home. Returns whether the
    /// press was consumed; only at the Home root does the activity fall through to the system
    /// default (finish).
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
        if (SelectedTab != MobileTab.Home) { SelectedTab = MobileTab.Home; return true; }
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

    [RelayCommand] private void OpenSongs() => Navigate(new SongListPageViewModel(this, "Nav.Songs", () => Library.Songs));

    [RelayCommand] private void OpenFavourites() => Navigate(new SongListPageViewModel(this, "Nav.Favorites", Library.Favourites) { OffersFavouritesFilter = false });

    [RelayCommand] private void OpenRecentlyAdded() => Navigate(new SongListPageViewModel(this, "Mobile.RecentlyAdded", Library.RecentlyAdded));

    [RelayCommand] private void OpenAlbums() => Navigate(new AlbumGridPageViewModel(this));

    [RelayCommand] private void OpenArtists() => Navigate(new ArtistListPageViewModel(this));

    [RelayCommand] private void OpenPlaylists() => Navigate(new PlaylistListPageViewModel(this));

    // Library sections' › headers.
    [RelayCommand] private void OpenPinned() => Navigate(new RailGridPageViewModel(this, "Mobile.Pinned", () => Library.PinnedRail));

    [RelayCommand] private void OpenOnRepeat() => Navigate(new SongListPageViewModel(this, "Mobile.OnRepeat", Library.OnRepeatTracks));

    [RelayCommand] private void OpenRecentlyPlayed() => Navigate(new RailGridPageViewModel(this, "Mobile.RecentlyPlayed", () => Library.RecentlyPlayedRail));

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
                // A song plays the rail it sits on: On Repeat, else the pinned songs.
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
        await LoadPlayerBarSettingsAsync();
        await Background.LoadAsync();
        await LoadHistoryPrefsAsync();
        // Lyrics settings before the queue restore: restoring sets CurrentTrack, which loads
        // that track's lyrics with the saved split-word and layer preferences.
        await Lyrics.InitializeAsync();
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
        if (Account is { IsSignedIn: true } account) StartupSync = SyncOnStartAsync(account);
        // Home's Last.fm sections (cached for 12 h: normally no request).
        _ = LastFm.RefreshAsync(force: false);
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
