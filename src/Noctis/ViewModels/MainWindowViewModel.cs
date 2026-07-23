using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Loon;

namespace Noctis.ViewModels;

/// <summary>
/// Top-level ViewModel that orchestrates the shell layout:
/// sidebar navigation, top search bar, content area routing, and the playback bar.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly ILibraryService _library;
    private readonly IPersistenceService _persistence;
    private readonly IMetadataService _metadata;
    private readonly IDiscordPresenceService _discord;
    private readonly ILastFmService _lastFm;
    private readonly IListenBrainzService _listenBrainz;
    private readonly ISyncService _syncService;
    private readonly ArtistImageService _artistImageService;
    private readonly LoonClient _loon;
    private readonly IPlayHistoryService _playHistory;

    // ── Settings modal ──
    [ObservableProperty] private bool _isSettingsModalOpen;

    /// <summary>Opens the Settings modal popup. Replaces page-based navigation to Settings.</summary>
    [RelayCommand]
    private void OpenSettings()
    {
        // Open immediately so the click feels instant. Library stats are an in-memory
        // pass (cheap); storage info walks the artwork directory and is deferred to
        // a background thread so it never blocks the click.
        IsSettingsModalOpen = true;
        Settings.RefreshLibraryStats();
        _ = Settings.RefreshStorageInfoAsync();
    }

    /// <summary>Closes the Settings modal popup.</summary>
    [RelayCommand]
    private void CloseSettings() => IsSettingsModalOpen = false;

    // ── Debug panel ──
    [ObservableProperty] private bool _isDebugPanelVisible;
    private DebugPanelViewModel? _debugPanelVm;

    /// <summary>ViewModel for the debug overlay panel (created on first toggle).</summary>
    public DebugPanelViewModel? DebugPanel => _debugPanelVm;

    // ── Scrobble tracking ──
    private DateTime _trackStartedAt;
    private Track? _scrobbleTrack;
    private readonly SemaphoreSlim _dropImportLock = new(1, 1);

    // ── Drop-import progress (spinner pill while dropped files are copied/added) ──
    [ObservableProperty] private bool _isDropImporting;
    [ObservableProperty] private string _dropImportStatus = string.Empty;

    // ── Discord seek throttle ──
    private DateTime _lastDiscordSeekUpdate = DateTime.MinValue;
    private static readonly TimeSpan DiscordSeekThrottle = TimeSpan.FromSeconds(1);

    // ── Top bar page actions (Songs page) ──
    private PropertyChangedEventHandler? _songsVmTopBarHandler;

    // ── Zone ViewModels (always alive) ──

    public SidebarViewModel Sidebar { get; }
    public TopBarViewModel TopBar { get; }
    public PlayerViewModel Player { get; }
    public SettingsViewModel Settings { get; }
    public LyricsViewModel Lyrics => _lyricsVm;

    // ── Content area ──

    /// <summary>The ViewModel currently displayed in the main content area (Zone C).</summary>
    [ObservableProperty] private ViewModelBase _currentView;

    /// <summary>Whether the lyrics view is currently active (hides the playback island bar).</summary>
    public bool IsLyricsViewActive => CurrentView == _lyricsVm;

    /// <summary>Whether the playback island bar should be visible (has content and not in lyrics view).</summary>
    public bool IsPlaybackBarVisible => Player.HasContent && !IsLyricsViewActive;

    /// <summary>Whether the playback island should stay mounted in the visual tree.</summary>
    public bool IsPlaybackBarMounted => Player.HasContent;

    /// <summary>Opacity used to hide the mounted playback bar while fullscreen lyrics owns playback controls.</summary>
    public double PlaybackBarOpacity => IsPlaybackBarVisible ? 1.0 : 0.0;

    /// <summary>Whether the mounted playback bar should accept pointer input.</summary>
    public bool IsPlaybackBarHitTestVisible => IsPlaybackBarVisible;

    // ── Sidebar visibility ──

    /// <summary>Whether the sidebar is hidden (toggled from lyrics view).</summary>
    [ObservableProperty] private bool _isSidebarHidden;

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarHidden = !IsSidebarHidden;

    // ── Lyrics side panel ──

    /// <summary>Whether the lyrics side panel overlay is open.</summary>
    [ObservableProperty] private bool _isLyricsPanelOpen;

    [RelayCommand]
    private void ToggleLyricsPanel()
    {
        if (IsLyricsViewActive) return;
        IsLyricsPanelOpen = !IsLyricsPanelOpen;
        if (IsLyricsPanelOpen)
        {
            // The lyrics VM reloads on TrackStarted regardless of visibility, but the
            // panel can open mid-track after a cold start — make sure lyrics exist and
            // the active line is current before the panel slides in.
            _lyricsVm.EnsureLyricsForCurrentTrack();
            Player.IsQueuePopupOpen = false;
        }
    }

    private sealed class NavigationEntry
    {
        public required ViewModelBase View { get; init; }
        public required string BackButtonText { get; init; }
        public required string TabName { get; init; }
        public required Action RestoreState { get; init; }
        public required string SearchText { get; init; }
        public required string SectionKey { get; init; }
    }

    private readonly Stack<NavigationEntry> _navigationHistory = new();
    // Forward stack mirrors browser semantics: populated when going back,
    // consumed when going forward, and cleared whenever a new branch is navigated.
    private readonly Stack<NavigationEntry> _forwardHistory = new();
    private string? _albumDetailBackButtonText;

    // ── Cached content ViewModels (created once, reused) ──

    private readonly HomeViewModel _homeVm;
    private readonly LibrarySongsViewModel _songsVm;
    private readonly LibraryAlbumsViewModel _albumsVm;
    private readonly LibraryArtistsViewModel _artistsVm;
    private readonly LibraryPlaylistsViewModel _playlistsVm;
    private readonly FavoritesViewModel _favoritesVm;
    private readonly LibraryFoldersViewModel _foldersVm;

    private readonly QueueViewModel _queueVm;
    private readonly LyricsViewModel _lyricsVm;
    private readonly StatisticsViewModel _statisticsVm;
    private readonly CoverFlowViewModel _coverFlowVm;
    /// <summary>True when the global Cover Flow overlay is active. Survives sidebar navigation between toggle-eligible sections; auto-exits when navigating to an ineligible section.</summary>
    private bool _isCoverFlowMode;

    /// <summary>The sidebar key for the section currently selected underneath Cover Flow (e.g. "home", "songs", "albums"). Tracked so clicking Library returns to the right section.</summary>
    private string _currentSectionKey = "home";

    /// <summary>The non-section view (e.g. a PlaylistViewModel) the user was on when entering Cover Flow. Restored on exit so clicking Library returns to the same detail page.</summary>
    private ViewModelBase? _preCoverFlowView;

    /// <summary>Sidebar keys whose section is allowed to display the Library/Cover Flow toggle.</summary>
    private static readonly HashSet<string> ToggleEligibleSections = new(StringComparer.Ordinal)
    {
        "home", "songs", "albums", "artists", "folders", "playlists", "favorites"
    };
    private string? _preLyricsViewKey;

    public MainWindowViewModel(
        ILibraryService library,
        IPersistenceService persistence,
        IAudioPlayer audioPlayer,
        IMetadataService metadata,
        IDiscordPresenceService discord,
        ILastFmService lastFm,
        IListenBrainzService listenBrainz,
        ISyncService syncService,
        ArtistImageService artistImageService,
        LoonClient loon,
        ILrcLibService lrcLib,
        INetEaseService netEase,
        IPlayHistoryService playHistory)
    {
        _library = library;
        _playHistory = playHistory;
        _persistence = persistence;
        _metadata = metadata;
        _discord = discord;
        _lastFm = lastFm;
        _listenBrainz = listenBrainz;
        _syncService = syncService;
        _artistImageService = artistImageService;
        _loon = loon;

        // Create long-lived ViewModels
        Player = new PlayerViewModel(audioPlayer, library, persistence, new AnimatedCoverService(persistence));
        Sidebar = new SidebarViewModel(persistence, library);
        TopBar = new TopBarViewModel();
        Sidebar.TopBar = TopBar;
        Settings = new SettingsViewModel(persistence, library, playHistory);
        Settings.SetAudioPlayer(audioPlayer);
        Settings.SetPlayer(Player);
        Player.SetSettingsViewModel(Settings);
        Player.SetPlayHistory(playHistory);
        Settings.SetDiscordPresence(discord);
        Settings.SetLoonClient(loon);
        Settings.SetLastFm(lastFm);
        Settings.SetListenBrainz(listenBrainz);
        Settings.SetUpdateService(App.Services!.GetRequiredService<UpdateService>());
        Settings.SettingsReset += async (_, _) =>
        {
            // Guarded: an unhandled throw from an async-void handler crashes the app.
            try { await Sidebar.LoadPlaylistsAsync(); }
            catch (Exception ex) { DebugLogger.Error(DebugLogger.Category.Error, "SettingsReset.ReloadPlaylists", ex.Message); }
        };

        // Mirror the "update available" state onto the Settings sidebar item so a
        // dot nudges the user without them opening Settings. The silent update
        // check runs on a background thread, so marshal to the UI thread.
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(SettingsViewModel.IsUpdateAvailable)) return;
            Dispatcher.UIThread.Post(() =>
            {
                var settingsNav = Sidebar.NavItems.FirstOrDefault(n => n.Key == "settings");
                if (settingsNav is not null)
                    settingsNav.ShowBadge = Settings.IsUpdateAvailable;
            });
        };

        // Create content ViewModels
        _homeVm = new HomeViewModel(Player, library, Sidebar, artistImageService, playHistory);
        _songsVm = new LibrarySongsViewModel(library, Player, Sidebar, persistence);
        _albumsVm = new LibraryAlbumsViewModel(library, Player, Sidebar, Settings);
        // Keep the top-bar dropdown labels in sync with the Albums grid filters/sort.
        _albumsVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LibraryAlbumsViewModel.AlbumSortLabel))
                TopBar.AlbumSortLabel = _albumsVm.AlbumSortLabel;
            else if (e.PropertyName == nameof(LibraryAlbumsViewModel.ReleaseTypeFilterLabel))
                TopBar.ReleaseTypeFilterLabel = _albumsVm.ReleaseTypeFilterLabel;
            else if (e.PropertyName == nameof(LibraryAlbumsViewModel.QualityFilterLabel))
                TopBar.QualityFilterLabel = _albumsVm.QualityFilterLabel;
        };
        _artistsVm = new LibraryArtistsViewModel(library);
        _artistsVm.SetArtistImageService(artistImageService);
        _playlistsVm = new LibraryPlaylistsViewModel(Sidebar, Player, library, persistence);

        _foldersVm = new LibraryFoldersViewModel(library, Player, persistence, Sidebar);
        _foldersVm.NavigateToSettingsRequested += (_, _) =>
        {
            OpenSettings();
            Dispatcher.UIThread.Post(Settings.RequestMediaFoldersSection);
        };
        // Settings is a modal overlay and folder add/remove doesn't fire LibraryUpdated,
        // so explicitly rebuild the Folders tree when the media-folder set changes.
        Settings.MusicFoldersChanged += (_, _) =>
        {
            // Keep the filesystem watchers in sync with the media-folder set.
            App.Services?.GetService<ILibraryWatcherService>()?.Refresh();
            Dispatcher.UIThread.Post(() =>
            {
                _foldersVm.MarkDirty();
                _foldersVm.Refresh();
            });
        };
        _favoritesVm = new FavoritesViewModel(Player, library, persistence, Sidebar);
        _queueVm = new QueueViewModel(Player);
        _lyricsVm = new LyricsViewModel(Player, lrcLib, netEase, metadata, persistence, library);
        _statisticsVm = new StatisticsViewModel(library, playHistory);
        _statisticsVm.BackRequested += (_, _) =>
        {
            // Return to whatever section the user was in before opening Settings →
            // "View All Stats" (Statistics isn't toggle-eligible, so _currentSectionKey
            // still holds that origin, e.g. "artists"), then reopen the Settings modal
            // on the Statistics tab they launched "View All Stats" from.
            Navigate(_currentSectionKey);
            OpenSettings();
        };
        Settings.OpenStatisticsRequested += (_, _) =>
        {
            CloseSettings();
            Navigate("statistics");
            // Repurpose the sidebar Back arrow to return to Settings. Using the standard
            // back-button path also hides the redundant "Statistics" page title.
            TopBar.ShowBackButton("Back", _statisticsVm.GoBackCommand);
        };
        _coverFlowVm = new CoverFlowViewModel(Player);

        // Default view
        _currentView = _homeVm;

        // Wire up navigation
        Sidebar.NavigationRequested += OnNavigationRequested;

        // Wire up lyrics navigation from player
        Player.SetNavigateAction(ToggleLyrics);

        // Wire up "View Album" from playback bar three-dots menu
        Player.SetViewAlbumAction(track =>
        {
            OnViewAlbumFromTrack(this, track);
            TopBar.IsPageTitleVisible = false;
        });

        // Wire up playlist access for playback bar
        Player.SetSidebar(Sidebar);

        // Wire up "View Artist" from lyrics view
        _lyricsVm.SetViewArtistAction(ViewArtistFromLyrics);
        _lyricsVm.SetViewAlbumAction(track => OnViewAlbumFromTrack(this, track));

        // Wire up search
        TopBar.SearchTextChanged += OnSearchTextChanged;

        // Wire up album detail navigation from albums view
        _albumsVm.AlbumOpened += OnAlbumOpened;
        _albumsVm.SetViewArtistAction(ViewArtistByName);

        // Entering/leaving an artist's discography toggles the title-bar filter chips.
        _albumsVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LibraryAlbumsViewModel.IsArtistFiltered))
                UpdateReleaseTypeChips();
        };

        // Wire up album detail navigation from home view
        _homeVm.AlbumOpened += OnHomeAlbumOpened;

        // Wire up artist → album navigation
        _artistsVm.ArtistOpened += OnArtistOpened;



        // Wire up playlist detail navigation from playlists view
        _playlistsVm.PlaylistOpened += OnPlaylistOpened;

        // Wire up "View Album" from Songs tab, Favorites tab, Home tab, and Folders tab
        _songsVm.ViewAlbumRequested += OnViewAlbumFromTrack;
        _favoritesVm.ViewAlbumRequested += OnViewAlbumFromTrack;
        _homeVm.ViewAlbumRequested += OnViewAlbumFromTrack;
        _foldersVm.ViewAlbumRequested += OnViewAlbumFromTrack;
        _favoritesVm.AlbumOpened += OnAlbumOpened;

        // Wire up Search Lyrics action on ViewModels with track context menus
        _homeVm.SetSearchLyricsAction(SearchLyricsForTrack);
        _songsVm.SetSearchLyricsAction(SearchLyricsForTrack);
        _foldersVm.SetSearchLyricsAction(SearchLyricsForTrack);
        Player.SetSearchLyricsAction(SearchLyricsForTrack);

        // Wire up View Artist action on ViewModels that display artist names
        _homeVm.SetViewArtistAction(ViewArtistByName);
        _songsVm.SetViewArtistAction(ViewArtistByName);
        _favoritesVm.SetViewArtistAction(ViewArtistByName);
        _foldersVm.SetViewArtistAction(ViewArtistByName);
        Player.SetViewArtistAction(ViewArtistByName);
        _coverFlowVm.SetViewArtistAction(ViewArtistByName);
        _coverFlowVm.SetViewAlbumAction(track => OnViewAlbumFromTrack(this, track));

        // Mirror the Cover Flow sub-mode (Carousel/Collage) into the top-bar pill segment.
        _coverFlowVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CoverFlowViewModel.IsCollageMode))
                TopBar.IsCollageMode = _coverFlowVm.IsCollageMode;
        };

        // Forward Player.HasContent changes to IsPlaybackBarVisible
        // and close lyrics panel when playback ends
        Player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlayerViewModel.HasContent))
            {
                OnPropertyChanged(nameof(IsPlaybackBarVisible));
                OnPropertyChanged(nameof(IsPlaybackBarMounted));
                OnPropertyChanged(nameof(PlaybackBarOpacity));
                OnPropertyChanged(nameof(IsPlaybackBarHitTestVisible));
            }
            if (e.PropertyName == nameof(PlayerViewModel.CurrentTrack) && Player.CurrentTrack == null)
                IsLyricsPanelOpen = false;
        };

        // Wire up Discord RPC and Last.fm integrations
        Player.TrackStarted += OnTrackStartedForIntegrations;
        Player.PropertyChanged += OnPlayerPropertyChangedForIntegrations;
        Player.Seeked += OnPlayerSeekedForIntegrations;

        // A loon reconnect rotates the clientId and invalidates the artwork URL Discord
        // is currently holding, so re-publish the presence with a fresh, valid URL.
        _loon.Reconnected += OnLoonReconnected;

    }

    /// <summary>
    /// Initializes the application: loads settings, library, playlists, and queue.
    /// Called from the View once the window is loaded.
    /// </summary>
    public async Task InitializeAsync()
    {
        // Load settings
        await Settings.LoadAsync();

        // Load persisted library
        await _library.LoadAsync();

        // Don't refresh every content ViewModel up front — Navigate() below
        // refreshes the destination page on its own, and the rest can warm
        // up on background priority after the window is interactive.
        // Refreshing all six VMs synchronously here was the main contributor
        // to slow startup on large libraries.

        // Load playlists into sidebar
        await Sidebar.LoadPlaylistsAsync();

        // Apply saved volume
        Player.Volume = Settings.GetSettings().Volume;

        // Restore the previous session's queue (current track loads paused;
        // the user presses play to resume). Gated by the Settings toggle.
        if (Settings.GetSettings().RestoreLastTrackOnStartup)
        {
            try { await Player.RestoreQueueStateAsync(); }
            catch (Exception ex) { Debug.WriteLine($"[MainWindowVM] Queue restore failed: {ex.Message}"); }
        }

        // Auto-scan if enabled
        if (Settings.GetSettings().ScanOnStartup && Settings.GetSettings().MusicFolders.Count > 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _library.ScanAsync(Settings.GetSettings().MusicFolders);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MainWindowVM] Auto-scan failed: {ex.Message}");
                }
            });
        }

        // Begin continuous folder watching now that settings + library are loaded.
        try { App.Services?.GetService<ILibraryWatcherService>()?.Refresh(); }
        catch (Exception ex) { Debug.WriteLine($"[MainWindowVM] Watcher start failed: {ex.Message}"); }

        // Silently check GitHub for a newer release so the About page can surface
        // a passive "Update available" badge without the user clicking anything.
        // Deferred + fire-and-forget so it never blocks startup.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                await Settings.CheckForUpdateSilentAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Update] Silent check failed: {ex.Message}");
            }
        });

        // Loon (Discord cover-art relay) is no longer connected unconditionally
        // here — SettingsViewModel connects/disconnects it with the Discord
        // presence toggle, so the remote channel only exists while it's needed.

        // Navigate to the user's preferred default page
        var defaultKey = Settings.GetDefaultPageKey();
        Navigate(defaultKey);

        // Pre-warm cached views and the non-visible content VMs on background
        // priority. The visible page was already refreshed by Navigate(); this
        // makes navigation to the other sections feel instant once the user
        // gets there, without blocking first paint.
        //
        // Each step is posted as its own work item so the dispatcher can service
        // input/render between them — doing all of this in one callback froze the
        // UI for ~1–2 s right after the window appeared (building a heavy view is
        // ~1 s on its own, plus six full list rebuilds).
        void PostWarmup(Action step) => Dispatcher.UIThread.Post(step, DispatcherPriority.Background);

        // Pre-warm only the heaviest cached views (Songs/Albums/Settings) so the
        // first click on those is instant. Trying to pre-warm every view here
        // saturated the dispatcher for ~10 s after launch — lighter views build
        // fast enough on first click that the cost isn't worth eating up front.
        // Wait ~1.5 s so the window has fully painted and the user has time to
        // orient before we start the background templating work.
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            Dispatcher.UIThread.Post(() => App.CachedLocator?.Build(_songsVm), DispatcherPriority.Background);
            Dispatcher.UIThread.Post(() => App.CachedLocator?.Build(_albumsVm), DispatcherPriority.Background);
            Dispatcher.UIThread.Post(() => App.CachedLocator?.Build(Settings), DispatcherPriority.Background);
        });

        // Refresh non-visible content VMs so their data is ready when navigated to.
        // These are data-only refreshes (no visual tree work), so they're cheap.
        PostWarmup(_songsVm.Refresh);
        PostWarmup(_albumsVm.Refresh);
        PostWarmup(_artistsVm.Refresh);
        PostWarmup(_foldersVm.Refresh);
        PostWarmup(_homeVm.Refresh);
        PostWarmup(_favoritesVm.Refresh);

        // Select the matching sidebar item
        var allNavItems = Sidebar.NavItems
            .Concat(Sidebar.FavoritesItems)
            .Concat(Sidebar.PlaylistItems);
        Sidebar.SelectedNavItem = allNavItems.FirstOrDefault(n => n.Key == defaultKey)
                                  ?? Sidebar.NavItems[0];

        // Play any files this launch was asked to open ("Open with Noctis"),
        // now that the window and player are up. Background priority so first
        // paint isn't delayed by the metadata read.
        if (App.PendingOpenFiles.Count > 0)
        {
            var pending = App.PendingOpenFiles;
            App.PendingOpenFiles = Array.Empty<string>();
            Dispatcher.UIThread.Post(() => OpenExternalFiles(pending), DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// Plays audio files handed to the app from outside ("Open with Noctis" on
    /// launch, or forwarded from a second launch through the single-instance
    /// pipe). Library entries are preferred so play counts/ratings attach;
    /// unknown files play from a direct metadata read without being imported.
    /// </summary>
    public void OpenExternalFiles(IReadOnlyList<string> paths)
    {
        var tracks = new List<Track>();
        foreach (var path in paths)
        {
            if (!File.Exists(path) ||
                !MetadataService.SupportedExtensions.Contains(Path.GetExtension(path)))
                continue;

            var existing = _library.Tracks.FirstOrDefault(t =>
                string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase));
            var track = existing ?? _metadata.ReadTrackMetadata(path);
            if (track != null)
                tracks.Add(track);
        }

        if (tracks.Count == 0) return;
        Player.ReplaceQueueAndPlay(tracks, 0);
    }

    /// <summary>
    /// Saves all application state before shutdown.
    /// </summary>
    public async Task ShutdownAsync()
    {
        // Stop any in-flight library scan and flush its progress to disk so the next
        // launch resumes where it left off instead of re-scanning from scratch.
        try { await _library.PauseActiveScanForShutdownAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) { Debug.WriteLine($"[MainWindowVM] Scan checkpoint failed: {ex.Message}"); }

        // Scrobble the currently playing track before shutdown
        TryScrobblePreviousTrack();

        // Update volume in settings and save everything. Each step is guarded
        // so one failing save can't skip the later ones (the queue snapshot
        // below used to be silently lost this way).
        Settings.SetVolume(Player.Volume);
        try { await Settings.SaveAsync(); }
        catch (Exception ex) { Debug.WriteLine($"[MainWindowVM] Settings save failed: {ex.Message}"); }
        try { await _playHistory.FlushAsync(); }
        catch (Exception ex) { Debug.WriteLine($"[MainWindowVM] Play-history flush failed: {ex.Message}"); }

        // Snapshot the queue so the next launch restores it.
        try { await Player.SaveQueueStateAsync(); }
        catch (Exception ex) { Debug.WriteLine($"[MainWindowVM] Queue save failed: {ex.Message}"); }

        // Flush the debounced per-play library save so play counts aren't lost.
        try { await Player.FlushPendingLibrarySaveAsync(); }
        catch (Exception ex) { Debug.WriteLine($"[MainWindowVM] Library flush failed: {ex.Message}"); }

        // Cleanup integrations
        await _discord.ClearAsync();
        _discord.Dispose();
        _loon.Dispose();
    }

    /// <summary>
    /// Imports files/folders dropped onto the app window.
    /// Folders are added to configured library roots and rescanned;
    /// standalone files are copied into the managed library folder, then imported.
    /// </summary>
    public async Task ImportDroppedMediaAsync(IEnumerable<string> droppedPaths, CancellationToken ct = default)
    {
        var input = (droppedPaths ?? Array.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();
        if (input.Count == 0) return;

        await _dropImportLock.WaitAsync(ct);
        try
        {
            IsDropImporting = true;
            DropImportStatus = "Preparing import…";

            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Per dropped folder: its audio files (original paths) and whether any sit
            // directly in the folder. A curated collection (downloaded playlist) has
            // files directly inside it; a library root only contains album subfolders.
            var folderAudioFiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var folderHasTopLevelAudio = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Maps each original file path to its final imported path (managed-root copy
            // or as-is), so we can resolve a folder's tracks after import.
            var originalToFinal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rawPath in input)
            {
                var normalized = TryNormalizePath(rawPath);
                System.Diagnostics.Debug.WriteLine($"[DropImport] rawPath={rawPath} → normalized={normalized}");
                if (string.IsNullOrWhiteSpace(normalized)) continue;

                if (Directory.Exists(normalized))
                {
                    System.Diagnostics.Debug.WriteLine($"[DropImport]   → is directory");
                    folders.Add(normalized);
                    continue;
                }

                if (!File.Exists(normalized))
                {
                    System.Diagnostics.Debug.WriteLine($"[DropImport]   → File.Exists=false, skipping");
                    continue;
                }
                var ext = Path.GetExtension(normalized);
                if (!MetadataService.SupportedExtensions.Contains(ext))
                {
                    System.Diagnostics.Debug.WriteLine($"[DropImport]   → unsupported ext '{ext}', skipping");
                    continue;
                }
                System.Diagnostics.Debug.WriteLine($"[DropImport]   → accepted as file");
                files.Add(normalized);
            }

            System.Diagnostics.Debug.WriteLine($"[DropImport] folders={folders.Count} files={files.Count}");
            if (folders.Count == 0 && files.Count == 0) return;

            // Expand dropped folders into their audio files so we can use
            // the additive ImportFilesAsync path instead of a full library rescan.
            // A full ScanAsync replaces the entire library with whatever is on disk
            // across ALL configured music folders, which can resurrect deleted files
            // or pull in unrelated media from other roots.
            // Enumeration is file I/O; run it off the UI thread (large folder trees).
            if (folders.Count > 0)
                DropImportStatus = "Scanning dropped folders…";
            await Task.Run(() =>
            {
            foreach (var folder in folders)
            {
                try
                {
                    var perFolder = new List<string>();
                    foreach (var file in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
                    {
                        var ext = Path.GetExtension(file);
                        if (!MetadataService.SupportedExtensions.Contains(ext)) continue;
                        files.Add(file);
                        perFolder.Add(file);

                        // Directly inside the dropped folder (not in a subfolder)?
                        if (string.Equals(Path.GetDirectoryName(file), folder, StringComparison.OrdinalIgnoreCase))
                            folderHasTopLevelAudio.Add(folder);
                    }
                    // Natural-ish order by file name so disc/track prefixes ("1-01", "1-02")
                    // become the playlist order.
                    perFolder.Sort((a, b) => string.Compare(
                        Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase));
                    folderAudioFiles[folder] = perFolder;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DropImport] Failed to enumerate folder {folder}: {ex.Message}");
                }
            }
            }, ct);

            if (files.Count == 0)
            {
                // Nothing playable in the drop (e.g. an album folder whose audio was
                // removed earlier and only cover art / .lrc remain). Silently doing
                // nothing here reads as a failed import — say so briefly instead.
                DropImportStatus = "No audio files found in the dropped items";
                try { await Task.Delay(2500, ct); } catch (OperationCanceledException) { }
                return;
            }

            if (files.Count > 0)
            {
                var managedRoot = await EnsureManagedImportRootAsync(ct);
                System.Diagnostics.Debug.WriteLine($"[DropImport] managedRoot={managedRoot}");

                var libraryRoots = Settings.GetSettings().MusicFolders
                    .Where(f => !string.IsNullOrWhiteSpace(f))
                    .Select(TryNormalizePath)
                    .Where(f => !string.IsNullOrWhiteSpace(f))
                    .Select(f => f!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                System.Diagnostics.Debug.WriteLine($"[DropImport] libraryRoots={string.Join("; ", libraryRoots)}");

                if (!string.IsNullOrWhiteSpace(managedRoot) &&
                    !libraryRoots.Contains(managedRoot, StringComparer.OrdinalIgnoreCase))
                {
                    libraryRoots.Add(managedRoot);
                }

                var importTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var beforeCount = _library.Tracks.Count;
                var copyProcessed = 0;
                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    DropImportStatus = $"Copying files {++copyProcessed} of {files.Count}";

                    // Files already inside configured library roots can be imported as-is.
                    if (libraryRoots.Any(root => IsPathUnderRoot(file, root)))
                    {
                        System.Diagnostics.Debug.WriteLine($"[DropImport]   {file} → already under library root, importing as-is");
                        importTargets.Add(file);
                        originalToFinal[file] = file;
                        continue;
                    }

                    // Managed import: copy external drops into library storage first
                    // (File.Copy is blocking I/O — keep it off the UI thread).
                    var copiedPath = string.IsNullOrWhiteSpace(managedRoot)
                        ? file
                        : await Task.Run(() => CopyFileIntoManagedRoot(file, managedRoot), ct);

                    System.Diagnostics.Debug.WriteLine($"[DropImport]   {file} → copied to: {copiedPath}");
                    if (!string.IsNullOrWhiteSpace(copiedPath))
                    {
                        importTargets.Add(copiedPath);
                        originalToFinal[file] = copiedPath!;
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[DropImport] importTargets={importTargets.Count} beforeCount={beforeCount}");
                if (importTargets.Count > 0)
                {
                    // Progress<T> posts back to the UI thread it was created on.
                    var importTotal = importTargets.Count;
                    var importProgress = new Progress<int>(n =>
                        DropImportStatus = $"Adding songs {Math.Min(n, importTotal)} of {importTotal}");
                    await _library.ImportFilesAsync(importTargets, ct, importProgress);
                    System.Diagnostics.Debug.WriteLine($"[DropImport] afterCount={_library.Tracks.Count}");

                    // If nothing new appeared (TagLib quirks, etc.), fallback to a targeted rescan.
                    if (_library.Tracks.Count == beforeCount && libraryRoots.Count > 0)
                    {
                        System.Diagnostics.Debug.WriteLine($"[DropImport] No new tracks, falling back to rescan");
                        DropImportStatus = "Refreshing library…";
                        await _library.ScanAsync(libraryRoots, ct);
                        System.Diagnostics.Debug.WriteLine($"[DropImport] Rescan done, count={_library.Tracks.Count}");
                    }
                }
            }

            // Copied drops lose sight of artwork images next to the ORIGINAL file
            // (cover.jpg etc.): extraction runs against the copy in the flat managed
            // root. For albums still missing art, retry from each file's source folder.
            // Album.ArtworkPath is only resolved during an index rebuild, and these
            // covers land after ImportFilesAsync's rebuild — without another rebuild
            // the album grid keeps its placeholder until the next launch.
            if (await BackfillDroppedFolderArtAsync(originalToFinal, ct))
                _library.NotifyMetadataChanged();

            // Force refresh to guarantee newly imported content is visible immediately.
            // Mark all VMs dirty first — the LibraryUpdated event may have already
            // posted a Refresh() that consumed the dirty flag before we get here.
            _songsVm.MarkDirty();
            _albumsVm.MarkDirty();
            _artistsVm.MarkDirty();
            _foldersVm.MarkDirty();
            _homeVm.MarkDirty();
            _favoritesVm.MarkDirty();

            _songsVm.Refresh();
            _albumsVm.Refresh();
            _artistsVm.Refresh();
            // Folders is built from the library track list too — without this the
            // dropped track lands in the library (Songs/Albums see it) but the folder
            // tree keeps its stale, cached state and never shows the new file. A later
            // Scan Library can't recover it either: the file is already imported, so
            // the scan no-ops without firing LibraryUpdated.
            _foldersVm.Refresh();

            _homeVm.Refresh();
            _favoritesVm.Refresh();
            Settings.RefreshLibraryStats();
            Settings.RefreshStorageInfo();

            // Hide the progress pill before the playlist-offer dialog can appear.
            IsDropImporting = false;

            await OfferFolderPlaylistsAsync(folderAudioFiles, folderHasTopLevelAudio, originalToFinal);
        }
        finally
        {
            IsDropImporting = false;
            DropImportStatus = string.Empty;
            _dropImportLock.Release();
        }
    }

    /// <summary>
    /// Extracts album art from the source location of copied drops. Only albums whose
    /// artwork is still missing after import are considered, so embedded art (already
    /// extracted from the copy) always wins over a source-folder image.
    /// Returns true when at least one artwork file was written.
    /// </summary>
    private async Task<bool> BackfillDroppedFolderArtAsync(
        Dictionary<string, string> originalToFinal, CancellationToken ct)
    {
        var copied = originalToFinal
            .Where(kv => !string.Equals(kv.Key, kv.Value, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (copied.Count == 0) return false;

        var tracksByPath = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in _library.Tracks)
        {
            if (!string.IsNullOrWhiteSpace(t.FilePath))
                tracksByPath[t.FilePath] = t;
        }

        var savedAny = false;
        await Task.Run(() =>
        {
            var checkedAlbums = new HashSet<Guid>();
            foreach (var (original, final) in copied)
            {
                ct.ThrowIfCancellationRequested();
                if (!tracksByPath.TryGetValue(final, out var track)) continue;
                if (!checkedAlbums.Add(track.AlbumId)) continue;

                try
                {
                    if (File.Exists(_persistence.GetArtworkPath(track.AlbumId))) continue;
                    var artBytes = _metadata.ExtractAlbumArt(original);
                    if (artBytes != null)
                    {
                        _persistence.SaveArtwork(track.AlbumId, artBytes);
                        savedAny = true;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[DropImport] Source-folder art backfill failed for {original}: {ex.Message}");
                }
            }
        }, ct);
        return savedAny;
    }

    /// <summary>
    /// After a folder drop, offers to keep a curated collection together as a playlist.
    /// Only triggers for a folder that directly contains audio files spanning two or
    /// more distinct albums (a downloaded playlist), not a single album or a library
    /// root made of album subfolders.
    /// </summary>
    private async Task OfferFolderPlaylistsAsync(
        Dictionary<string, List<string>> folderAudioFiles,
        HashSet<string> folderHasTopLevelAudio,
        Dictionary<string, string> originalToFinal)
    {
        if (folderAudioFiles.Count == 0) return;

        // Resolve imported tracks by their final file path.
        var tracksByPath = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in _library.Tracks)
        {
            if (!string.IsNullOrWhiteSpace(t.FilePath))
                tracksByPath[t.FilePath] = t;
        }

        foreach (var (folder, originalFiles) in folderAudioFiles)
        {
            if (!folderHasTopLevelAudio.Contains(folder)) continue;

            // Map this folder's files (in name order) to imported tracks.
            var tracks = new List<Track>(originalFiles.Count);
            foreach (var original in originalFiles)
            {
                if (originalToFinal.TryGetValue(original, out var final) &&
                    tracksByPath.TryGetValue(final, out var track))
                {
                    tracks.Add(track);
                }
            }

            if (tracks.Count < 2) continue;

            // A single-album folder is a normal album — leave it alone.
            var distinctAlbums = tracks.Select(t => t.AlbumId).Distinct().Count();
            if (distinctAlbums < 2) continue;

            var name = Path.GetFileName(folder.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(name)) continue;

            // Don't re-prompt if a playlist with this name already exists (e.g. re-drop).
            if (Sidebar.ManualPlaylistExists(name)) continue;

            var confirmed = await Views.ConfirmationDialog.ShowAsync(
                $"\"{name}\" contains {tracks.Count} tracks from {distinctAlbums} different albums.\n\n" +
                $"Create a playlist named \"{name}\" with these tracks?");
            if (!confirmed) continue;

            await Sidebar.CreatePlaylistFromTracksAsync(name, tracks);
        }
    }

    partial void OnCurrentViewChanged(ViewModelBase? oldValue, ViewModelBase newValue)
    {
        var enteringLyrics = ReferenceEquals(newValue, _lyricsVm);
        var leavingLyrics = ReferenceEquals(oldValue, _lyricsVm) && !enteringLyrics;

        if (enteringLyrics)
        {
            // The full-page lyrics view supersedes the side panel.
            IsLyricsPanelOpen = false;
            NotifyPlaybackBarPresentationChanged();
            WireLyricsPageToPlayer();
        }

        // Keep Player.IsLyricsPageActive in sync with the actual view so the inline
        // playback bar's TrackInfoPanel hides/shows correctly across all nav paths.
        if (leavingLyrics)
        {
            UnwireLyricsPageFromPlayer();
            NotifyPlaybackBarPresentationChanged();
        }
        else if (!enteringLyrics)
        {
            NotifyPlaybackBarPresentationChanged();
        }

        if (newValue is not AlbumDetailViewModel)
            _albumDetailBackButtonText = null;

        RefreshBackButton();
        UpdateReleaseTypeChips();

        // Dispose transient ViewModels (e.g., AlbumDetailViewModel) to release
        // event handlers on singleton services and unmanaged resources like Bitmaps.
        // BUT: skip disposal if the old view is being kept in navigation history
        // for back-navigation (e.g., album detail → artist discography → back).
        // Also skip cached singletons — disposing them kills long-lived event
        // subscriptions (e.g., Songs VM stops receiving LibraryUpdated).
        DisposeViewIfTransient(oldValue);
    }

    private void NotifyPlaybackBarPresentationChanged()
    {
        OnPropertyChanged(nameof(IsLyricsViewActive));
        OnPropertyChanged(nameof(IsPlaybackBarVisible));
        OnPropertyChanged(nameof(IsPlaybackBarMounted));
        OnPropertyChanged(nameof(PlaybackBarOpacity));
        OnPropertyChanged(nameof(IsPlaybackBarHitTestVisible));
    }

    private void RefreshBackButton()
    {
        TopBar.SearchWatermark = CurrentView switch
        {
            PlaylistFeaturedArtistsViewModel => "Search in Featured Artists",
            MoreByArtistViewModel => "Search in Albums",
            _ => $"Search in {TopBar.CurrentTabName}"
        };

        // Search visibility follows the tab name (hidden on Home/Settings/Lyrics and
        // in Cover Flow), except the More By page which is searchable regardless of
        // the tab it was opened from (the tab name doesn't change on detail pages).
        TopBar.IsSearchVisible = CurrentView is MoreByArtistViewModel
            || (!_isCoverFlowMode && TopBar.CurrentTabName is not ("Home" or "Settings" or "Lyrics"));

        if (CurrentView is MoreByArtistViewModel mbaVm)
        {
            TopBar.ShowBackButton("Back", GoBackInHistoryCommand, mbaVm.Title);
            return;
        }

        if (CurrentView is PlaylistFeaturedArtistsViewModel featuredArtistsVm)
        {
            TopBar.ShowBackButton("Back", GoBackInHistoryCommand, featuredArtistsVm.Title);
            return;
        }

        if (CurrentView is AlbumDetailViewModel)
        {
            TopBar.ShowAlbumDetailBackButton(GetCurrentAlbumDetailBackButtonText(), GoBackInHistoryCommand);
            return;
        }

        if (ReferenceEquals(CurrentView, _albumsVm) && _albumsVm.IsArtistFiltered)
        {
            TopBar.ShowBackButton("Back", GoBackInHistoryCommand, _albumsVm.HeaderText);
            return;
        }

        if (_navigationHistory.Count > 0 && ShouldShowTopBarBackButton(CurrentView))
        {
            var backButtonText = _navigationHistory.Peek().BackButtonText;
            if (string.IsNullOrWhiteSpace(backButtonText))
                backButtonText = GetRootBackButtonText(CurrentView);

            TopBar.ShowBackButton(backButtonText, GoBackInHistoryCommand);
            return;
        }

        if (CurrentView is PlaylistViewModel)
        {
            TopBar.ShowBackButton("Back", new RelayCommand(() => Navigate("playlists")));
            return;
        }

        TopBar.HideBackButton();
    }

    private bool ShouldShowTopBarBackButton(ViewModelBase? view)
    {
        return view is AlbumDetailViewModel

               || (ReferenceEquals(view, _albumsVm) && _albumsVm.IsArtistFiltered)
               || (_navigationHistory.Count > 0 && view is ISearchable && !string.IsNullOrWhiteSpace(TopBar.SearchText));
    }

    private void DisposeViewIfTransient(ViewModelBase? view)
    {
        if (view is not IDisposable disposable
            || IsLongLivedView(view)
            || ReferenceEquals(view, CurrentView)
            || IsViewInHistory(view))
        {
            return;
        }

        disposable.Dispose();
    }

    private bool IsViewInHistory(ViewModelBase? view)
    {
        if (view == null)
            return false;

        return _navigationHistory.Any(entry => ReferenceEquals(entry.View, view))
               || _forwardHistory.Any(entry => ReferenceEquals(entry.View, view));
    }

    private void ClearForwardHistory()
    {
        while (_forwardHistory.Count > 0)
        {
            var entry = _forwardHistory.Pop();
            DisposeViewIfTransient(entry.View);
        }
    }

    private NavigationEntry CaptureCurrentNavigationEntry()
    {
        var view = CurrentView;
        var tabName = TopBar.CurrentTabName;
        var backButtonText = GetNavigationEntryBackButtonText(view);
        var restoreState = CaptureRestoreState(view);
        var sectionKey = GetNavigationEntrySectionKey(view);

        return new NavigationEntry
        {
            View = view,
            BackButtonText = backButtonText,
            TabName = tabName,
            RestoreState = restoreState,
            SearchText = TopBar.SearchText,
            SectionKey = sectionKey
        };
    }

    private bool IsAlbumsRootSnapshot()
    {
        return (ReferenceEquals(CurrentView, _albumsVm) || ReferenceEquals(CurrentView, _coverFlowVm))
               && !_albumsVm.IsArtistFiltered
               && string.IsNullOrWhiteSpace(TopBar.SearchText);
    }

    private Action CaptureRestoreState(ViewModelBase view)
    {
        if (ReferenceEquals(view, _coverFlowVm))
        {
            return () =>
            {
                _isCoverFlowMode = true;
                TopBar.IsCoverFlowMode = true;
                TopBar.IsSearchVisible = false;
            };
        }

        if (view is MoreByArtistViewModel mba)
        {
            return () =>
            {
                TopBar.ShowArtistActions(mba.ShuffleAllCommand, mba.PlayAllCommand);
            };
        }

        if (ReferenceEquals(view, _albumsVm))
        {
            var artistFilterName = _albumsVm.ArtistFilterName;
            var searchQuery = TopBar.SearchText;
            // Read lazily because the view saves its physical scroll offset after
            // this entry is pushed, when it detaches from the visual tree.

            return () =>
            {
                var scrollOffset = string.IsNullOrWhiteSpace(artistFilterName)
                    ? (_albumsVm.SavedUnfilteredScrollOffset > 0
                        ? _albumsVm.SavedUnfilteredScrollOffset
                        : _albumsVm.SavedScrollOffset)
                    : _albumsVm.SavedScrollOffset;

                if (string.IsNullOrWhiteSpace(artistFilterName))
                {
                    _albumsVm.ClearArtistFilter();
                    _albumsVm.ApplyFilterImmediate(searchQuery ?? string.Empty);
                }
                else
                {
                    _albumsVm.SetArtistFilter(artistFilterName);
                    TopBar.ShowArtistActions(
                        _albumsVm.ShuffleAllArtistTracksCommand,
                        _albumsVm.PlayAllArtistTracksCommand);
                }

                _albumsVm.SavedScrollOffset = scrollOffset;
            };
        }

        return () => { };
    }

    private void PushCurrentViewToHistory()
    {
        PushHistoryEntry(CaptureCurrentNavigationEntry());
        // Navigating to a new branch invalidates any forward history.
        ClearForwardHistory();
    }

    /// <summary>Every navigation (including sidebar section switches) is history-eligible,
    /// so the stack is capped to keep transient detail VMs from accumulating forever.</summary>
    private const int MaxNavigationHistory = 30;

    private void PushHistoryEntry(NavigationEntry entry)
    {
        _navigationHistory.Push(entry);
        if (_navigationHistory.Count <= MaxNavigationHistory)
            return;

        var kept = _navigationHistory.Take(MaxNavigationHistory).ToList();
        var dropped = _navigationHistory.Skip(MaxNavigationHistory).ToList();
        _navigationHistory.Clear();
        for (var i = kept.Count - 1; i >= 0; i--)
            _navigationHistory.Push(kept[i]);
        foreach (var old in dropped)
            DisposeViewIfTransient(old.View);
    }

    public bool CanGoBack => _navigationHistory.Count > 0;
    public bool CanGoForward => _forwardHistory.Count > 0;

    [RelayCommand]
    private void GoBackInHistory()
    {
        if (_navigationHistory.Count == 0)
            return;

        // Preserve the current view so forward navigation can return to it.
        _forwardHistory.Push(CaptureCurrentNavigationEntry());
        RestoreNavigationEntry(_navigationHistory.Pop());
    }

    [RelayCommand]
    private void GoForwardInHistory()
    {
        if (_forwardHistory.Count == 0)
            return;

        // Mirror of GoBack: stash current onto the back stack, then restore the
        // forward entry. Forward stack is intentionally left intact here.
        _navigationHistory.Push(CaptureCurrentNavigationEntry());
        RestoreNavigationEntry(_forwardHistory.Pop());
    }

    private void RestoreNavigationEntry(NavigationEntry target)
    {
        ClearAllTopBarActions();
        target.RestoreState();
        TopBar.CurrentTabName = target.TabName;
        TopBar.SearchText = target.SearchText;
        _currentSectionKey = target.SectionKey;
        _albumDetailBackButtonText = target.View is AlbumDetailViewModel
            ? NormalizeAlbumDetailBackButtonText(target.BackButtonText)
            : null;

        if (!ReferenceEquals(CurrentView, target.View))
            CurrentView = target.View;

        // Restore page-specific top bar actions for the target view
        RestoreTopBarActionsForView(target.View);
        RefreshBackButton();
        SyncSidebarSelectionToHistoryEntry(target);
    }

    /// <summary>Keeps the sidebar highlight in sync when Back/Forward restores a view —
    /// the sidebar only updates its own selection on direct clicks.</summary>
    private void SyncSidebarSelectionToHistoryEntry(NavigationEntry target)
    {
        NavItem? item = target.View is PlaylistViewModel playlistView
            ? Sidebar.PlaylistItems.FirstOrDefault(n => n.PlaylistId == playlistView.PlaylistId)
            : Sidebar.NavItems.FirstOrDefault(n => n.Key == target.SectionKey)
              ?? Sidebar.FavoritesItems.FirstOrDefault(n => n.Key == target.SectionKey);

        // Views without a sidebar entry (queue, lyrics, …) keep the current highlight.
        if (item != null)
            Sidebar.SetSelectedNavItemSilently(item);
    }

    private string GetRootBackButtonText(ViewModelBase view)
    {
        if (ReferenceEquals(view, _homeVm))
            return GetSectionBackButtonText("home");
        if (ReferenceEquals(view, _albumsVm) || ReferenceEquals(view, _coverFlowVm))
            return _albumsVm.IsArtistFiltered ? "Back" : GetSectionBackButtonText("albums");
        if (ReferenceEquals(view, _songsVm))
            return GetSectionBackButtonText("songs");
        if (ReferenceEquals(view, _artistsVm))
            return GetSectionBackButtonText("artists");
        if (ReferenceEquals(view, _foldersVm))
            return GetSectionBackButtonText("folders");
        if (ReferenceEquals(view, _playlistsVm))
            return GetSectionBackButtonText("playlists");
        if (ReferenceEquals(view, _favoritesVm))
            return GetSectionBackButtonText("favorites");
        if (ReferenceEquals(view, _queueVm))
            return GetSectionBackButtonText("queue");
        if (ReferenceEquals(view, _lyricsVm))
            return GetSectionBackButtonText("lyrics");
        if (ReferenceEquals(view, _statisticsVm))
            return GetSectionBackButtonText("statistics");
        if (ReferenceEquals(view, Settings))
            return GetSectionBackButtonText("settings");
        if (view is AlbumDetailViewModel)
            return "Back";
        if (view is PlaylistViewModel)
            return "Back";
        return "Back";
    }

    private string GetCurrentSectionBackButtonText()
    {
        return GetSectionBackButtonText(GetCurrentSectionKey());
    }

    private string GetCurrentSectionKey()
    {
        return _currentSectionKey;
    }

    private static string GetSectionBackButtonText(string? key)
    {
        _ = key;
        return "Back";
    }

    private string GetNavigationEntryBackButtonText(ViewModelBase view)
    {
        return view is AlbumDetailViewModel
            ? GetCurrentAlbumDetailBackButtonText()
            : GetRootBackButtonText(view);
    }

    private string GetNavigationEntrySectionKey(ViewModelBase view)
    {
        if (ReferenceEquals(view, _homeVm))
            return "home";
        if (ReferenceEquals(view, _songsVm))
            return "songs";
        if (ReferenceEquals(view, _albumsVm) || ReferenceEquals(view, _coverFlowVm))
            return "albums";
        if (ReferenceEquals(view, _artistsVm))
            return "artists";
        if (ReferenceEquals(view, _foldersVm))
            return "folders";
        if (ReferenceEquals(view, _playlistsVm))
            return "playlists";
        if (ReferenceEquals(view, _favoritesVm))
            return "favorites";
        if (ReferenceEquals(view, _queueVm))
            return "queue";
        if (ReferenceEquals(view, _lyricsVm))
            return "lyrics";
        if (ReferenceEquals(view, _statisticsVm))
            return "statistics";
        if (ReferenceEquals(view, Settings))
            return "settings";

        return _currentSectionKey;
    }

    private string GetCurrentAlbumDetailBackButtonText()
    {
        return NormalizeAlbumDetailBackButtonText(_albumDetailBackButtonText ?? GetAlbumDetailBackButtonText());
    }

    private string GetAlbumDetailBackButtonText()
    {
        if (_navigationHistory.Count == 0)
            return GetCurrentSectionBackButtonText();

        var entry = _navigationHistory.Peek();
        var text = entry.BackButtonText;

        if (string.IsNullOrWhiteSpace(text) || text == "Back")
            text = GetRootBackButtonText(entry.View);

        var sourceSectionKey = entry.SectionKey;

        if (string.IsNullOrWhiteSpace(text)
            && !ReferenceEquals(entry.View, _albumsVm)
            && !ReferenceEquals(entry.View, _coverFlowVm)
            && entry.View is not AlbumDetailViewModel)
        {
            text = GetSectionBackButtonText(sourceSectionKey);
        }

        return NormalizeAlbumDetailBackButtonText(text);
    }

    private static string NormalizeAlbumDetailBackButtonText(string? text)
    {
        return string.IsNullOrWhiteSpace(text) ? "Back" : text;
    }

    private bool IsLongLivedView(ViewModelBase? view)
    {
        return ReferenceEquals(view, _homeVm)
               || ReferenceEquals(view, _songsVm)
               || ReferenceEquals(view, _albumsVm)
               || ReferenceEquals(view, _artistsVm)
               || ReferenceEquals(view, _foldersVm)
               || ReferenceEquals(view, _playlistsVm)
               || ReferenceEquals(view, _favoritesVm)

               || ReferenceEquals(view, _queueVm)
               || ReferenceEquals(view, _lyricsVm)
               || ReferenceEquals(view, _statisticsVm)
               || ReferenceEquals(view, _coverFlowVm)
               || ReferenceEquals(view, Settings);
    }

    // ── Navigation ───────────────────────────────────────────

    private void OnNavigationRequested(object? sender, string key)
    {
        // Settings is a modal popup rather than a page — intercept and open the overlay,
        // then restore the sidebar selection to the actual current section so the highlight
        // doesn't lie about where the user is. Use Post so the click's selection has settled.
        if (key == "settings")
        {
            OpenSettings();
            var currentKey = GetCurrentViewKey();
            Dispatcher.UIThread.Post(() =>
            {
                var item = Sidebar.NavItems.FirstOrDefault(n => n.Key == currentKey)
                        ?? Sidebar.FavoritesItems.FirstOrDefault(n => n.Key == currentKey);
                Sidebar.SetSelectedNavItemSilently(item);
            });
            return;
        }
        Navigate(key);
    }

    [RelayCommand]
    private void Navigate(string key)
    {
        DebugLogger.Info(DebugLogger.Category.UI, "Navigate", $"key={key}, from={GetCurrentViewKey()}, coverFlow={_isCoverFlowMode}");
        // Section switches join the browser-style history (instead of clearing it)
        // so Back/Forward work across top-level views, e.g. Songs → Album → Artists.
        // Captured before any state mutation below; pushed only if the view changes.
        var previousEntry = CurrentView != null ? CaptureCurrentNavigationEntry() : null;

        var goingToEligibleSection = ToggleEligibleSections.Contains(key);

        // If we're in Cover Flow and the user clicks an ineligible destination
        // (album detail, settings, lyrics, etc.), auto-exit Cover Flow first
        // so the toggle hide and the destination view land in a consistent state.
        if (_isCoverFlowMode && !goingToEligibleSection)
        {
            _isCoverFlowMode = false;
            TopBar.IsCoverFlowMode = false;
            TopBar.IsSearchVisible = _currentSectionKey != "home";
            _preCoverFlowView = null;
        }

        // Track which top-level section the user is conceptually on. While in
        // Cover Flow this is the section "underneath" the overlay.
        if (goingToEligibleSection)
            _currentSectionKey = key;

        // Resolve the destination view. If we're staying in Cover Flow (eligible
        // destination + sticky mode), the visible content stays as the carousel;
        // only the underlying section key changes.
        if (_isCoverFlowMode && goingToEligibleSection)
        {
            // Touch the underlying section so its data is fresh when the user
            // exits Cover Flow (mirrors what Navigate would normally do).
            _ = ResolveSectionView(key);
            // CurrentView stays as _coverFlowVm.
        }
        else
        {
            CurrentView = key switch
            {
                "home" => RefreshAndReturn(_homeVm),
                "songs" => RefreshAndReturnSongs(_songsVm),
                "albums" => ResetFilterAndReturnAlbums(),
                "artists" => ResetAndReturnArtists(),
                "folders" => RefreshAndReturnFolders(_foldersVm),
                "playlists" => RefreshAndReturnPlaylists(_playlistsVm),
                "favorites" => RefreshAndReturnFavorites(_favoritesVm),
                "statistics" => RefreshAndReturnStatistics(_statisticsVm),
                "queue" => _queueVm,
                "lyrics" => EnsureLyricsAndReturn(_lyricsVm),
                "settings" => RefreshAndReturnSettings(),
                _ when key.StartsWith("playlist:") => CreatePlaylistView(key),
                _ => _homeVm
            };
        }

        if (previousEntry != null && !ReferenceEquals(CurrentView, previousEntry.View))
        {
            PushHistoryEntry(previousEntry);
            ClearForwardHistory();
        }

        // Clear search when switching views. Queue popup stays open across navigation —
        // it's only dismissed by toggling the Queue button itself or by Escape.
        TopBar.SearchText = string.Empty;
        // Assigning SearchText only dispatches ApplyFilter when the box value actually
        // changes (and via a debounce). If the box was already empty from a prior visit,
        // the destination view keeps whatever filter it was last given — so it can show
        // nothing even though the search box looks empty. Clear it directly here.
        if (CurrentView is ISearchable searchableView)
            searchableView.ApplyFilter(string.Empty);

        TopBar.CurrentTabName = key switch
        {
            "home" => "Home",
            "songs" => "Songs",
            "albums" => "Albums",
            "artists" => "Artists",
            "folders" => "Folders",
            "playlists" => "Playlists",
            "favorites" => "Favorites",
            "statistics" => "Statistics",
            "queue" => "Queue",
            "lyrics" => "Lyrics",
            "settings" => "Settings",
            _ when key.StartsWith("playlist:") => "Playlist",
            _ => "Library"
        };

        // Clear all top bar actions, then set up the correct ones for the destination
        ClearAllTopBarActions();
        if (key == "lyrics") WireLyricsPageToPlayer();
        if (goingToEligibleSection)
            SetupGlobalViewModeToggle();
        if (key == "songs")
            SetupSongsTopBarActions();
        else if (key == "playlists")
            TopBar.ShowPlaylistActions(Sidebar.CreatePlaylistCommand, _playlistsVm.CreateSmartPlaylistCommand, _playlistsVm.ImportPlaylistCommand);
        else if (key == "favorites")
            TopBar.ShowFavoritesActions(_favoritesVm.ShuffleAllCommand, _favoritesVm.PlayAllCommand);
        else if (key == "folders")
            TopBar.ShowFoldersActions(_foldersVm.PlayFolderCommand, _foldersVm.ShuffleFolderCommand, _foldersVm.OpenMediaFolderSettingsCommand);

        RefreshBackButton();
    }

    private LyricsViewModel EnsureLyricsAndReturn(LyricsViewModel vm)
    {
        vm.EnsureLyricsForCurrentTrack();
        return vm;
    }

    private HomeViewModel RefreshAndReturn(HomeViewModel vm)
    {
        vm.Refresh();
        return vm;
    }

    private LibraryArtistsViewModel ResetAndReturnArtists()
    {
        _artistsVm.SearchText = string.Empty;
        _artistsVm.IsSearchVisible = false;
        _artistsVm.Refresh();
        return _artistsVm;
    }

    private LibraryAlbumsViewModel ResetFilterAndReturnAlbums()
    {
        // Clear any stale artist filter from OnArtistOpened so the user
        // sees the full album grid when navigating via the sidebar.
        _albumsVm.ClearArtistFilter();
        _albumsVm.Refresh();
        return _albumsVm;
    }

    private LibraryFoldersViewModel RefreshAndReturnFolders(LibraryFoldersViewModel vm)
    {
        vm.Refresh();
        return vm;
    }

    private LibrarySongsViewModel RefreshAndReturnSongs(LibrarySongsViewModel vm)
    {
        vm.Refresh();
        return vm;
    }


    private LibraryPlaylistsViewModel RefreshAndReturnPlaylists(LibraryPlaylistsViewModel vm)
    {
        vm.Refresh();
        return vm;
    }

    private FavoritesViewModel RefreshAndReturnFavorites(FavoritesViewModel vm)
    {
        vm.Refresh();
        return vm;
    }

    private StatisticsViewModel RefreshAndReturnStatistics(StatisticsViewModel vm)
    {
        vm.Refresh();
        return vm;
    }

    private SettingsViewModel RefreshAndReturnSettings()
    {
        Settings.RefreshLibraryStats();
        Settings.RefreshStorageInfo();
        return Settings;
    }

    /// <summary>Navigate to the Now Playing view (from clicking album art in playback bar).</summary>
    /// <summary>Opens the Ctrl+K command palette over the current window.</summary>
    public async Task OpenCommandPaletteAsync()
    {
        var vm = new CommandPaletteViewModel(this, _library);
        await Views.CommandPaletteDialog.ShowAsync(vm);
    }

    [RelayCommand]
    private void OpenNowPlaying()
    {
        PushCurrentViewToHistory();

        var nowPlaying = new NowPlayingViewModel(Player, Settings);
        nowPlaying.BackRequested += (_, _) => GoBackInHistory();
        CurrentView = nowPlaying;
    }

    private void OnAlbumOpened(object? sender, Album album)
    {
        OpenAlbumDetail(album);
    }

    private void OnHomeAlbumOpened(object? sender, Album album)
    {
        OpenAlbumDetail(album);
    }

    private void OnArtistOpened(object? sender, Artist artist)
    {
        OpenArtistDiscography(artist.Name);
    }

    /// <summary>Opens the artist's discography grid from any artist credit string.</summary>
    public void OpenArtistByName(string artistName)
    {
        OpenArtistDiscography(Track.GetPrimaryArtist(artistName));
    }


    private void OnPlaylistOpened(object? sender, Playlist playlist)
    {
        PushCurrentViewToHistory();
        ClearAllTopBarActions();

        // Re-scope the top-bar search to the detail view (the history entry above
        // captured the grid's query, so Back restores it). Without this the grid's
        // stale query lingers in the box and the watermark stays "Search in Playlists".
        TopBar.SearchText = string.Empty;
        TopBar.CurrentTabName = "Playlist";

        var detail = new PlaylistViewModel(playlist, Player, _library, _persistence, Sidebar, _artistImageService);
        detail.BackRequested += (_, _) => GoBackInHistory();
        detail.ViewAlbumRequested += OnViewAlbumFromTrack;
        detail.SetSearchLyricsAction(SearchLyricsForTrack);
        detail.SetViewArtistAction(ViewArtistByName);
        detail.SetOpenFeaturedArtistsAction(OpenPlaylistFeaturedArtistsPage);
        CurrentView = detail;
    }

    private void OnViewAlbumFromTrack(object? sender, Track track)
    {
        // Find the album that contains this track
        var album = _library.Albums.FirstOrDefault(a => a.Id == track.AlbumId);
        if (album == null) return;

        OpenAlbumDetail(album);
    }

    public void OpenAlbumDetail(Album album, bool pushHistory = true, string? backButtonText = null)
    {
        if (pushHistory)
            PushCurrentViewToHistory();
        _albumDetailBackButtonText = NormalizeAlbumDetailBackButtonText(backButtonText ?? GetAlbumDetailBackButtonText());
        ClearAllTopBarActions();
        IsLyricsPanelOpen = false;
        _isCoverFlowMode = false;
        TopBar.IsCoverFlowMode = false;
        _preCoverFlowView = null;

        var detail = new AlbumDetailViewModel(album, Player, _persistence, _library, Sidebar, _lastFm, Settings);
        detail.BackRequested += (_, _) => GoBackInHistory();
        detail.ViewAlbumRequested += OnViewAlbumFromTrack;
        detail.SetViewArtistAction(ViewArtistFromAlbumDetail);
        detail.SetSearchLyricsAction(SearchLyricsForTrack);
        detail.SetOpenMoreByArtistAction(OpenMoreByArtistPage);
        CurrentView = detail;
        TopBar.ShowAlbumDetailBackButton(GetCurrentAlbumDetailBackButtonText(), GoBackInHistoryCommand);
    }

    private void OpenMoreByArtistPage(string artistName, System.Collections.Generic.IEnumerable<Album> albums)
    {
        PushCurrentViewToHistory();
        ClearAllTopBarActions();
        // The originating page's query is captured in history for back-restore;
        // the More By grid starts unfiltered.
        TopBar.SearchText = string.Empty;

        var page = new MoreByArtistViewModel(artistName, albums, Player, _albumsVm);
        page.BackRequested += (_, _) => GoBackInHistory();
        page.AlbumOpened += (_, album) => OpenAlbumDetail(album);
        CurrentView = page;
        TopBar.ShowArtistActions(page.ShuffleAllCommand, page.PlayAllCommand);
        RefreshBackButton();
    }

    private void OpenArtistDiscography(string artistName)
    {
        PushCurrentViewToHistory();
        ClearAllTopBarActions();
        SetupGlobalViewModeToggle();
        _albumsVm.SetArtistFilter(artistName);

        if (!ReferenceEquals(CurrentView, _albumsVm))
            CurrentView = _albumsVm;

        TopBar.ShowArtistActions(
            _albumsVm.ShuffleAllArtistTracksCommand,
            _albumsVm.PlayAllArtistTracksCommand);

        RefreshBackButton();
    }

    private ViewModelBase CreatePlaylistView(string key)
    {
        // key format: "playlist:{guid}"
        var idStr = key.Replace("playlist:", "");
        if (!Guid.TryParse(idStr, out var id)) return _homeVm;

        var playlist = Sidebar.GetPlaylist(id);
        if (playlist == null) return _homeVm;

        var playlistVm = new PlaylistViewModel(playlist, Player, _library, _persistence, Sidebar, _artistImageService);
        playlistVm.ViewAlbumRequested += OnViewAlbumFromTrack;
        playlistVm.BackRequested += (_, _) => Navigate("playlists");
        playlistVm.SetSearchLyricsAction(SearchLyricsForTrack);
        playlistVm.SetViewArtistAction(ViewArtistByName);
        playlistVm.SetOpenFeaturedArtistsAction(OpenPlaylistFeaturedArtistsPage);
        return playlistVm;
    }

    private void OpenPlaylistFeaturedArtistsPage(IReadOnlyList<PlaylistFeaturedArtist> artists)
    {
        if (artists.Count == 0) return;

        PushCurrentViewToHistory();
        ClearAllTopBarActions();
        // The originating page's query is captured in history for back-restore;
        // the featured-artists grid starts unfiltered.
        TopBar.SearchText = string.Empty;

        var page = new PlaylistFeaturedArtistsViewModel(artists);
        page.SetViewArtistAction(ViewArtistByName);
        CurrentView = page;
        RefreshBackButton();
    }

    // ── Search ───────────────────────────────────────────────

    private void OnSearchTextChanged(object? sender, string query)
    {
        DebugLogger.Info(DebugLogger.Category.Search, "ApplyFilter", $"query=\"{query}\", target={CurrentView?.GetType().Name}");
        if (CurrentView is ISearchable searchable)
        {
            searchable.ApplyFilter(query);
        }
    }

    private void SetupSongsTopBarActions()
    {
        ClearTopBarPageActions();
        TopBar.ShowPageActions(
            _songsVm.ShuffleAllCommand,
            new RelayCommand(() => Player.IsQueuePopupOpen = !Player.IsQueuePopupOpen),
            _songsVm.ShowOnlyFavorites,
            _songsVm.SortAscending,
            _songsVm.SortColumn,
            _songsVm.SetShowAllItemsCommand,
            _songsVm.SetShowOnlyFavoritesCommand,
            _songsVm.SortCommand);
        TopBar.ShowSongsFilters(_songsVm.SummaryText, _songsVm.QualityFilter, _songsVm.SetQualityFilterCommand);
        _songsVmTopBarHandler = (_, e) =>
        {
            if (e.PropertyName == nameof(LibrarySongsViewModel.ShowOnlyFavorites))
                TopBar.PageShowOnlyFavorites = _songsVm.ShowOnlyFavorites;
            else if (e.PropertyName == nameof(LibrarySongsViewModel.SortAscending))
                TopBar.PageSortAscending = _songsVm.SortAscending;
            else if (e.PropertyName == nameof(LibrarySongsViewModel.SortColumn))
                TopBar.PageSortColumn = _songsVm.SortColumn;
            else if (e.PropertyName == nameof(LibrarySongsViewModel.SummaryText))
                TopBar.SongsSummaryText = _songsVm.SummaryText;
            else if (e.PropertyName == nameof(LibrarySongsViewModel.QualityFilter))
                TopBar.SongsQualityFilter = _songsVm.QualityFilter;
        };
        _songsVm.PropertyChanged += _songsVmTopBarHandler;
    }

    private void ClearTopBarPageActions()
    {
        if (_songsVmTopBarHandler != null)
        {
            _songsVm.PropertyChanged -= _songsVmTopBarHandler;
            _songsVmTopBarHandler = null;
        }
        TopBar.HidePageActions();
        TopBar.HideSongsFilters();
    }

    /// <summary>Clears all page-specific, playlist, artist, and view-mode top bar actions.</summary>
    private void ClearAllTopBarActions()
    {
        UnwireLyricsPageFromPlayer();
        ClearTopBarPageActions();
        TopBar.HidePlaylistActions();
        TopBar.HideArtistActions();
        TopBar.HideFavoritesActions();
        TopBar.HideFoldersActions();
        TopBar.HideViewModeToggle();
    }

    private void WireLyricsPageToPlayer()
    {
        Player.SetLyricsPageActions(
            selectSynced: () => _lyricsVm.SelectSyncedLyricsCommand.Execute(null),
            selectPlain: () => _lyricsVm.SelectPlainLyricsCommand.Execute(null),
            openBackgroundColor: () => _lyricsVm.OpenBackgroundColorPickerCommand.Execute(null),
            removeLyrics: () => _lyricsVm.RemoveLyricsCommand.Execute(null),
            shareLyrics: () => _lyricsVm.ShareLyricsCommand.Execute(null),
            isSyncedActive: _lyricsVm.IsSyncTabSelected,
            isPlainActive: _lyricsVm.IsUnsyncTabSelected,
            isSyncedAvailable: _lyricsVm.HasSyncedLyricsAvailable,
            canShare: _lyricsVm.ShareAvailable);

        _lyricsVm.PropertyChanged -= OnLyricsVmPropertyChanged;
        _lyricsVm.PropertyChanged += OnLyricsVmPropertyChanged;
    }

    private void UnwireLyricsPageFromPlayer()
    {
        _lyricsVm.PropertyChanged -= OnLyricsVmPropertyChanged;
        Player.ClearLyricsPageActions();
    }

    private void OnLyricsVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LyricsViewModel.IsSyncTabSelected)
            || e.PropertyName == nameof(LyricsViewModel.IsUnsyncTabSelected)
            || e.PropertyName == nameof(LyricsViewModel.HasSyncedLyricsAvailable)
            || e.PropertyName == nameof(LyricsViewModel.ShareAvailable))
        {
            Player.UpdateLyricsPageState(
                isSyncedActive: _lyricsVm.IsSyncTabSelected,
                isPlainActive: _lyricsVm.IsUnsyncTabSelected,
                isSyncedAvailable: _lyricsVm.HasSyncedLyricsAvailable,
                canShare: _lyricsVm.ShareAvailable);
        }
    }

    /// <summary>
    /// Shows the global Library/Cover Flow toggle in the top bar and reflects the current mode.
    /// Call after navigating to a toggle-eligible section.
    /// </summary>
    private void SetupGlobalViewModeToggle()
    {
        TopBar.ShowViewModeToggle(
            new RelayCommand(ExitCoverFlowMode),
            new RelayCommand(EnterCoverFlowMode),
            _isCoverFlowMode,
            _coverFlowVm.ToggleCollageModeCommand,
            _coverFlowVm.IsCollageMode);

        // Home and Cover Flow do not use the top-bar search field.
        if (_isCoverFlowMode || _currentSectionKey == "home")
            TopBar.IsSearchVisible = false;
    }

    /// <summary>
    /// Shows the release-type filter chips on the Albums grid (Library mode),
    /// including the artist discography page. Cover Flow hides them.
    /// </summary>
    private void UpdateReleaseTypeChips()
    {
        var show = ReferenceEquals(CurrentView, _albumsVm)
                   && !_isCoverFlowMode;
        if (show)
        {
            TopBar.ShowReleaseTypeChips(_albumsVm.ReleaseTypeChips, _albumsVm.SelectReleaseTypeChipCommand,
                _albumsVm.QualityChips, _albumsVm.SelectQualityChipCommand, _albumsVm.SetAlbumSortCommand,
                _albumsVm.SetReleaseTypeFilterCommand, _albumsVm.SetQualityFilterCommand);
            TopBar.AlbumSortLabel = _albumsVm.AlbumSortLabel;
            TopBar.ReleaseTypeFilterLabel = _albumsVm.ReleaseTypeFilterLabel;
            TopBar.QualityFilterLabel = _albumsVm.QualityFilterLabel;
        }
        else
        {
            TopBar.HideReleaseTypeChips();
        }
    }

    private void EnterCoverFlowMode()
    {
        if (_isCoverFlowMode) return;
        // Remember non-section views (e.g. a playlist detail) so exit returns to them
        // instead of dumping the user back to the section list.
        _preCoverFlowView = CurrentView is PlaylistViewModel ? CurrentView : null;
        _isCoverFlowMode = true;
        TopBar.IsCoverFlowMode = true;
        TopBar.IsSearchVisible = false;
        CurrentView = _coverFlowVm;
    }

    private void ExitCoverFlowMode()
    {
        if (!_isCoverFlowMode) return;
        _isCoverFlowMode = false;
        TopBar.IsCoverFlowMode = false;
        TopBar.IsSearchVisible = _currentSectionKey != "home";
        if (_preCoverFlowView != null)
        {
            CurrentView = _preCoverFlowView;
            RestoreTopBarActionsForView(_preCoverFlowView);
            _preCoverFlowView = null;
        }
        else
        {
            // Return to the section that was selected underneath
            CurrentView = ResolveSectionView(_currentSectionKey);
        }
    }

    /// <summary>Resolves a sidebar section key to the cached long-lived ViewModel for that section. Refreshes content as needed (mirrors Navigate's switch).</summary>
    private ViewModelBase ResolveSectionView(string key) => key switch
    {
        "home"      => RefreshAndReturn(_homeVm),
        "songs"     => RefreshAndReturnSongs(_songsVm),
        "albums"    => ResetFilterAndReturnAlbums(),
        "artists"   => ResetAndReturnArtists(),
        "folders"   => RefreshAndReturnFolders(_foldersVm),
        "playlists" => RefreshAndReturnPlaylists(_playlistsVm),
        "favorites" => RefreshAndReturnFavorites(_favoritesVm),
        _           => RefreshAndReturn(_homeVm)
    };

    /// <summary>Restores the correct top bar actions when navigating back to a view.</summary>
    private void RestoreTopBarActionsForView(ViewModelBase view)
    {
        // The toggle is shown for any of the 7 long-lived section views or while in Cover Flow.
        if (ReferenceEquals(view, _homeVm)
            || ReferenceEquals(view, _songsVm)
            || ReferenceEquals(view, _albumsVm)
            || ReferenceEquals(view, _artistsVm)
            || ReferenceEquals(view, _foldersVm)
            || ReferenceEquals(view, _playlistsVm)
            || ReferenceEquals(view, _favoritesVm)
            || ReferenceEquals(view, _coverFlowVm))
        {
            SetupGlobalViewModeToggle();
        }

        if (ReferenceEquals(view, _songsVm))
            SetupSongsTopBarActions();
        else if (ReferenceEquals(view, _playlistsVm))
            TopBar.ShowPlaylistActions(Sidebar.CreatePlaylistCommand, _playlistsVm.CreateSmartPlaylistCommand, _playlistsVm.ImportPlaylistCommand);
        else if (ReferenceEquals(view, _favoritesVm))
            TopBar.ShowFavoritesActions(_favoritesVm.ShuffleAllCommand, _favoritesVm.PlayAllCommand);
        else if (ReferenceEquals(view, _lyricsVm))
            WireLyricsPageToPlayer();
        // Artist actions for _albumsVm are restored in CaptureRestoreState's lambda
    }

    private string GetCurrentViewKey()
    {
        if (CurrentView == _homeVm) return "home";
        if (CurrentView == _songsVm) return "songs";
        if (CurrentView == _albumsVm || CurrentView == _coverFlowVm) return "albums";
        if (CurrentView == _artistsVm) return "artists";
        if (CurrentView == _foldersVm) return "folders";

        if (CurrentView == _playlistsVm) return "playlists";
        if (CurrentView == _favoritesVm) return "favorites";
        if (CurrentView == _queueVm) return "queue";
        if (CurrentView == _lyricsVm) return "lyrics";
        if (CurrentView == _statisticsVm) return "statistics";
        if (CurrentView == Settings) return "settings";
        return "home";
    }

    private async Task<string?> EnsureManagedImportRootAsync(CancellationToken ct)
    {
        try
        {
            var normalizedRoots = Settings.GetSettings().MusicFolders
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(TryNormalizePath)
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => f!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Prefer an existing configured folder that already exists on disk.
            var existingRoot = normalizedRoots.FirstOrDefault(Directory.Exists);
            if (!string.IsNullOrWhiteSpace(existingRoot))
                return existingRoot;

            // If configured roots exist but don't exist on disk yet, use the first one.
            var configuredRoot = normalizedRoots.FirstOrDefault();
            var musicFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            if (string.IsNullOrEmpty(musicFolder))
                musicFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music");
            var fallbackRoot = Path.Combine(musicFolder, "Noctis Imports");

            var targetRoot = configuredRoot ?? fallbackRoot;
            var normalizedTarget = TryNormalizePath(targetRoot) ?? targetRoot;

            Directory.CreateDirectory(normalizedTarget);

            var alreadyConfigured = normalizedRoots.Contains(normalizedTarget, StringComparer.OrdinalIgnoreCase);
            if (!alreadyConfigured)
                await Settings.AddFolderPath(normalizedTarget);

            ct.ThrowIfCancellationRequested();
            return normalizedTarget;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindowVM] Failed to prepare managed import root: {ex.Message}");
            return null;
        }
    }

    private static string? CopyFileIntoManagedRoot(string sourceFile, string managedRoot)
    {
        try
        {
            var sourcePath = TryNormalizePath(sourceFile);
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                return null;

            var rootPath = TryNormalizePath(managedRoot) ?? managedRoot;
            Directory.CreateDirectory(rootPath);

            var fileName = Path.GetFileName(sourcePath);
            var destinationPath = Path.Combine(rootPath, fileName);
            destinationPath = TryNormalizePath(destinationPath) ?? destinationPath;

            // Prevent path traversal: ensure destination stays within managed root
            var fullDestination = Path.GetFullPath(destinationPath);
            var fullRoot = Path.GetFullPath(rootPath);
            if (!fullDestination.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Debug.WriteLine($"[DropImport] Path traversal blocked: {destinationPath}");
                return null;
            }

            // Already in the managed root.
            if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
                return sourcePath;

            if (File.Exists(destinationPath))
            {
                var sourceInfo = new FileInfo(sourcePath);
                var destinationInfo = new FileInfo(destinationPath);

                // Reuse existing copy if it appears to be the same file payload.
                if (sourceInfo.Length == destinationInfo.Length &&
                    sourceInfo.LastWriteTimeUtc == destinationInfo.LastWriteTimeUtc)
                {
                    return destinationPath;
                }

                var baseName = Path.GetFileNameWithoutExtension(fileName);
                var extension = Path.GetExtension(fileName);
                var suffix = 2;

                do
                {
                    destinationPath = Path.Combine(rootPath, $"{baseName} ({suffix}){extension}");
                    suffix++;
                } while (File.Exists(destinationPath));
            }

            File.Copy(sourcePath, destinationPath, overwrite: false);
            File.SetLastWriteTimeUtc(destinationPath, File.GetLastWriteTimeUtc(sourcePath));
            return destinationPath;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindowVM] Managed import copy failed: {ex.Message}");
            return null;
        }
    }

    private static bool IsPathUnderRoot(string path, string root)
    {
        var normalizedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryNormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return null;
        }
    }

    private void ViewArtistFromLyrics(string artistName)
    {
        OpenArtistByName(artistName);
    }

    private void ViewArtistFromAlbumDetail(string artistName)
    {
        OpenArtistByName(artistName);
    }

    /// <summary>Navigates to the artist discography page from any track/artist link click.</summary>
    private void ViewArtistByName(string artistName)
    {
        OpenArtistByName(artistName);
    }

    /// <summary>Toggles between lyrics view and previous view.</summary>
    [RelayCommand]
    private void ToggleLyrics(string? key)
    {
        if (CurrentView == _lyricsVm)
        {
            // Return to the view that was active before lyrics
            if (_navigationHistory.Count > 0)
                GoBackInHistory();
            else
                Navigate(_preLyricsViewKey ?? "home");
        }
        else
        {
            _preLyricsViewKey = GetCurrentViewKey();
            // Push current view to history so we can restore it (including detail views)
            PushCurrentViewToHistory();
            // Set lyrics directly — Navigate() would clear the search/tab state we
            // want restored when toggling back out of lyrics.
            EnsureLyricsAndReturn(_lyricsVm);
            CurrentView = _lyricsVm;
            Player.IsQueuePopupOpen = false;
            TopBar.SearchText = string.Empty;
            TopBar.CurrentTabName = "Lyrics";
            ClearAllTopBarActions();
            WireLyricsPageToPlayer();
        }
    }

    /// <summary>Navigates to lyrics view and searches for lyrics for the given track.</summary>
    private void SearchLyricsForTrack(Track track)
    {
        _preLyricsViewKey = GetCurrentViewKey();
        PushCurrentViewToHistory();
        EnsureLyricsAndReturn(_lyricsVm);
        CurrentView = _lyricsVm;
        Player.IsQueuePopupOpen = false;
        TopBar.SearchText = string.Empty;
        TopBar.CurrentTabName = "Lyrics";
        ClearAllTopBarActions();
        WireLyricsPageToPlayer();
        _lyricsVm.SearchLyricsForTrack(track);
    }

    // ── Discord RPC / Last.fm integration ─────────────────────

    private void OnTrackStartedForIntegrations(object? sender, Track track)
    {
        // Scrobble the *previous* track if it was played long enough
        TryScrobblePreviousTrack();

        // Record new track start for scrobble tracking
        _scrobbleTrack = track;
        _trackStartedAt = DateTime.UtcNow;

        // Update Discord presence
        if (_discord.IsConnected)
        {
            _ = UpdateDiscordPresenceAsync(track, TimeSpan.Zero, true);
        }

        // Update Last.fm Now Playing
        if (_lastFm.IsAuthenticated && Settings.LastFmScrobblingEnabled)
            _ = _lastFm.UpdateNowPlayingAsync(track);

        // Update ListenBrainz Now Playing (independent of Last.fm)
        if (_listenBrainz.IsAuthenticated && Settings.ListenBrainzScrobblingEnabled)
            _ = _listenBrainz.UpdateNowPlayingAsync(track);

        // Queue play-state sync for enabled server-backed connections.
        _ = _syncService.PushPlayStateAsync(track);
    }

    private void OnPlayerPropertyChangedForIntegrations(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Player.State))
        {
            // Update Discord presence for state changes.
            // ClearAsync must be checked BEFORE the CurrentTrack null guard because
            // AdvanceQueueCore nulls CurrentTrack before setting State=Stopped.
            if (_discord.IsConnected)
            {
                if (Player.State == PlaybackState.Stopped || Player.State == PlaybackState.Paused)
                {
                    _ = _discord.ClearAsync();
                }
                else if (Player.State == PlaybackState.Playing && Player.CurrentTrack != null)
                {
                    _ = UpdateDiscordPresenceAsync(Player.CurrentTrack, Player.Position, true);
                }
            }

            // If stopped, try to scrobble the track that just ended
            if (Player.State == PlaybackState.Stopped)
                TryScrobblePreviousTrack();
        }
    }

    private void OnPlayerSeekedForIntegrations(object? sender, TimeSpan newPosition)
    {
        if (!_discord.IsConnected || Player.CurrentTrack == null) return;

        var now = DateTime.UtcNow;
        if (now - _lastDiscordSeekUpdate < DiscordSeekThrottle) return;
        _lastDiscordSeekUpdate = now;

        _ = UpdateDiscordPresenceAsync(Player.CurrentTrack, newPosition, Player.State == PlaybackState.Playing);
    }

    private void OnLoonReconnected()
    {
        // Refresh the presence so Discord re-fetches the cover through the new clientId.
        if (!_discord.IsConnected) return;

        var track = Player.CurrentTrack;
        if (track == null || Player.State != PlaybackState.Playing) return;

        _ = UpdateDiscordPresenceAsync(track, Player.Position, true);
    }

    private async Task UpdateDiscordPresenceAsync(Track track, TimeSpan position, bool isPlaying)
    {
        try
        {
            var artworkUrl = _loon.GetArtworkUrl(track.AlbumArtworkPath);

            var dto = new DiscordPresenceTrack(
                track.Title ?? "Unknown",
                track.Artist ?? "Unknown Artist",
                track.Album,
                track.Duration,
                artworkUrl);
            await _discord.UpdateAsync(dto, position, track.Duration, isPlaying);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Discord] Presence update failed: {ex.Message}");
        }
    }

    private void TryScrobblePreviousTrack()
    {
        if (_scrobbleTrack == null)
            return;

        var lastFmActive = _lastFm.IsAuthenticated && Settings.LastFmScrobblingEnabled;
        var listenBrainzActive = _listenBrainz.IsAuthenticated && Settings.ListenBrainzScrobblingEnabled;
        if (!lastFmActive && !listenBrainzActive)
        {
            _scrobbleTrack = null;
            return;
        }

        var elapsed = DateTime.UtcNow - _trackStartedAt;
        var duration = _scrobbleTrack.Duration;

        // Last.fm scrobble rules: played > 50% of duration OR > 4 minutes.
        // ListenBrainz mirrors the same threshold for consistency.
        bool shouldScrobble = duration.TotalSeconds > 0
            && (elapsed.TotalSeconds > duration.TotalSeconds * 0.5 || elapsed.TotalMinutes > 4);

        if (shouldScrobble)
        {
            var track = _scrobbleTrack;
            var startedAt = _trackStartedAt;
            if (lastFmActive)
                _ = _lastFm.ScrobbleAsync(track, startedAt);
            if (listenBrainzActive)
                _ = _listenBrainz.ScrobbleAsync(track, startedAt);
        }

        _scrobbleTrack = null;
    }

    // ── Debug panel ──────────────────────────────────────────

    /// <summary>Toggles the debug overlay panel and enables/disables logging.</summary>
    public void ToggleDebugPanel()
    {
        if (_debugPanelVm == null)
        {
            _debugPanelVm = new DebugPanelViewModel(Player, this);
            OnPropertyChanged(nameof(DebugPanel));
        }

        IsDebugPanelVisible = !IsDebugPanelVisible;
        DebugLogger.IsEnabled = true; // keep logging even when panel closes
        DebugLogger.Info(DebugLogger.Category.UI, IsDebugPanelVisible ? "DebugPanel opened" : "DebugPanel closed");
    }
}


