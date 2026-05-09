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
    private readonly IDiscordPresenceService _discord;
    private readonly ILastFmService _lastFm;
    private readonly ISyncService _syncService;
    private readonly ArtistImageService _artistImageService;
    private readonly LoonClient _loon;

    // ── Debug panel ──
    [ObservableProperty] private bool _isDebugPanelVisible;
    private DebugPanelViewModel? _debugPanelVm;

    /// <summary>ViewModel for the debug overlay panel (created on first toggle).</summary>
    public DebugPanelViewModel? DebugPanel => _debugPanelVm;

    // ── Scrobble tracking ──
    private DateTime _trackStartedAt;
    private Track? _scrobbleTrack;
    private readonly SemaphoreSlim _dropImportLock = new(1, 1);

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
            Player.IsQueuePopupOpen = false;
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
        ISyncService syncService,
        ArtistImageService artistImageService,
        LoonClient loon,
        ILrcLibService lrcLib,
        INetEaseService netEase)
    {
        _library = library;
        _persistence = persistence;
        _discord = discord;
        _lastFm = lastFm;
        _syncService = syncService;
        _artistImageService = artistImageService;
        _loon = loon;

        // Create long-lived ViewModels
        Player = new PlayerViewModel(audioPlayer, library, persistence, new AnimatedCoverService(persistence));
        Sidebar = new SidebarViewModel(persistence, library);
        TopBar = new TopBarViewModel();
        Settings = new SettingsViewModel(persistence, library);
        Settings.SetAudioPlayer(audioPlayer);
        Settings.SetPlayer(Player);
        Player.SetSettingsViewModel(Settings);
        Settings.SetDiscordPresence(discord);
        Settings.SetLoonClient(loon);
        Settings.SetLastFm(lastFm);
        Settings.SetArtistImageService(artistImageService);
        Settings.SetUpdateService(App.Services!.GetRequiredService<UpdateService>());
        Settings.SettingsReset += async (_, _) => await Sidebar.LoadPlaylistsAsync();

        // Create content ViewModels
        _homeVm = new HomeViewModel(Player, library, Sidebar);
        _songsVm = new LibrarySongsViewModel(library, Player, Sidebar, persistence);
        _albumsVm = new LibraryAlbumsViewModel(library, Player, Sidebar);
        _artistsVm = new LibraryArtistsViewModel(library);
        _artistsVm.SetArtistImageService(artistImageService);
        _playlistsVm = new LibraryPlaylistsViewModel(Sidebar, Player, library, persistence);

        _foldersVm = new LibraryFoldersViewModel(library, Player, persistence);
        _foldersVm.NavigateToSettingsRequested += (_, _) =>
        {
            Navigate("settings");
            Dispatcher.UIThread.Post(Settings.RequestMediaFoldersSection);
        };
        _favoritesVm = new FavoritesViewModel(Player, library, persistence, Sidebar);
        _queueVm = new QueueViewModel(Player);
        _lyricsVm = new LyricsViewModel(Player, lrcLib, netEase, metadata, persistence, library);
        _statisticsVm = new StatisticsViewModel(library);
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
            var album = _library.Albums.FirstOrDefault(a => a.Id == track.AlbumId);
            if (album == null) return;
            OpenAlbumDetail(album, pushHistory: true);
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

        // Wire up album detail navigation from home view
        _homeVm.AlbumOpened += OnHomeAlbumOpened;

        // Wire up artist → album navigation
        _artistsVm.ArtistOpened += OnArtistOpened;



        // Wire up playlist detail navigation from playlists view
        _playlistsVm.PlaylistOpened += OnPlaylistOpened;

        // Wire up "View Album" from Songs tab, Favorites tab, and Home tab
        _songsVm.ViewAlbumRequested += OnViewAlbumFromTrack;
        _favoritesVm.ViewAlbumRequested += OnViewAlbumFromTrack;
        _homeVm.ViewAlbumRequested += OnViewAlbumFromTrack;
        _favoritesVm.AlbumOpened += OnAlbumOpened;

        // Wire up Search Lyrics action on ViewModels with track context menus
        _homeVm.SetSearchLyricsAction(SearchLyricsForTrack);
        _songsVm.SetSearchLyricsAction(SearchLyricsForTrack);
        _favoritesVm.SetSearchLyricsAction(SearchLyricsForTrack);
        Player.SetSearchLyricsAction(SearchLyricsForTrack);

        // Wire up View Artist action on ViewModels that display artist names
        _homeVm.SetViewArtistAction(ViewArtistByName);
        _songsVm.SetViewArtistAction(ViewArtistByName);
        _favoritesVm.SetViewArtistAction(ViewArtistByName);
        Player.SetViewArtistAction(ViewArtistByName);
        _coverFlowVm.SetViewArtistAction(ViewArtistByName);
        _coverFlowVm.SetViewAlbumAction(track => OnViewAlbumFromTrack(this, track));

        // Forward Player.HasContent changes to IsPlaybackBarVisible
        // and close lyrics panel when playback ends
        Player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlayerViewModel.HasContent))
                OnPropertyChanged(nameof(IsPlaybackBarVisible));
            if (e.PropertyName == nameof(PlayerViewModel.CurrentTrack) && Player.CurrentTrack == null)
                IsLyricsPanelOpen = false;
        };

        // Wire up Discord RPC and Last.fm integrations
        Player.TrackStarted += OnTrackStartedForIntegrations;
        Player.PropertyChanged += OnPlayerPropertyChangedForIntegrations;
        Player.Seeked += OnPlayerSeekedForIntegrations;

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

        // Connect loon client for Discord cover art
        var loonUrl = Settings.GetSettings().LoonServerUrl;
        if (string.IsNullOrWhiteSpace(loonUrl))
            loonUrl = "http://noctis-loon.duckdns.org";
        _ = Task.Run(async () =>
        {
            try
            {
                await _loon.ConnectAsync(loonUrl);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Loon] Connection failed: {ex.Message}");
            }
        });

        // Navigate to the user's preferred default page
        var defaultKey = Settings.GetDefaultPageKey();
        Navigate(defaultKey);

        // Pre-warm cached views and the non-visible content VMs on background
        // priority. The visible page was already refreshed by Navigate(); this
        // makes navigation to the other sections feel instant once the user
        // gets there, without blocking first paint.
        Dispatcher.UIThread.Post(() =>
        {
            App.CachedLocator?.Build(_songsVm);
            App.CachedLocator?.Build(_albumsVm);
            _songsVm.Refresh();
            _albumsVm.Refresh();
            _artistsVm.Refresh();
            _foldersVm.Refresh();
            _homeVm.Refresh();
            _favoritesVm.Refresh();
        }, DispatcherPriority.Background);

        // Select the matching sidebar item
        var allNavItems = Sidebar.NavItems
            .Concat(Sidebar.FavoritesItems)
            .Concat(Sidebar.PlaylistItems);
        Sidebar.SelectedNavItem = allNavItems.FirstOrDefault(n => n.Key == defaultKey)
                                  ?? Sidebar.NavItems[0];
    }

    /// <summary>
    /// Saves all application state before shutdown.
    /// </summary>
    public async Task ShutdownAsync()
    {
        // Scrobble the currently playing track before shutdown
        TryScrobblePreviousTrack();

        // Update volume in settings and save everything
        Settings.SetVolume(Player.Volume);
        await Settings.SaveAsync();

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
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
            foreach (var folder in folders)
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
                    {
                        var ext = Path.GetExtension(file);
                        if (MetadataService.SupportedExtensions.Contains(ext))
                            files.Add(file);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DropImport] Failed to enumerate folder {folder}: {ex.Message}");
                }
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
                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();

                    // Files already inside configured library roots can be imported as-is.
                    if (libraryRoots.Any(root => IsPathUnderRoot(file, root)))
                    {
                        System.Diagnostics.Debug.WriteLine($"[DropImport]   {file} → already under library root, importing as-is");
                        importTargets.Add(file);
                        continue;
                    }

                    // Managed import: copy external drops into library storage first.
                    var copiedPath = string.IsNullOrWhiteSpace(managedRoot)
                        ? file
                        : CopyFileIntoManagedRoot(file, managedRoot);

                    System.Diagnostics.Debug.WriteLine($"[DropImport]   {file} → copied to: {copiedPath}");
                    if (!string.IsNullOrWhiteSpace(copiedPath))
                        importTargets.Add(copiedPath);
                }

                System.Diagnostics.Debug.WriteLine($"[DropImport] importTargets={importTargets.Count} beforeCount={beforeCount}");
                if (importTargets.Count > 0)
                {
                    await _library.ImportFilesAsync(importTargets, ct);
                    System.Diagnostics.Debug.WriteLine($"[DropImport] afterCount={_library.Tracks.Count}");

                    // If nothing new appeared (TagLib quirks, etc.), fallback to a targeted rescan.
                    if (_library.Tracks.Count == beforeCount && libraryRoots.Count > 0)
                    {
                        System.Diagnostics.Debug.WriteLine($"[DropImport] No new tracks, falling back to rescan");
                        await _library.ScanAsync(libraryRoots, ct);
                        System.Diagnostics.Debug.WriteLine($"[DropImport] Rescan done, count={_library.Tracks.Count}");
                    }
                }
            }

            // Force refresh to guarantee newly imported content is visible immediately.
            // Mark all VMs dirty first — the LibraryUpdated event may have already
            // posted a Refresh() that consumed the dirty flag before we get here.
            _songsVm.MarkDirty();
            _albumsVm.MarkDirty();
            _artistsVm.MarkDirty();
            _homeVm.MarkDirty();
            _favoritesVm.MarkDirty();

            _songsVm.Refresh();
            _albumsVm.Refresh();
            _artistsVm.Refresh();

            _homeVm.Refresh();
            _favoritesVm.Refresh();
            Settings.RefreshLibraryStats();
            Settings.RefreshStorageInfo();
        }
        finally
        {
            _dropImportLock.Release();
        }
    }

    partial void OnCurrentViewChanged(ViewModelBase? oldValue, ViewModelBase newValue)
    {
        // Notify island bar visibility
        OnPropertyChanged(nameof(IsLyricsViewActive));
        OnPropertyChanged(nameof(IsPlaybackBarVisible));

        // Close lyrics panel when entering lyrics full-screen view
        if (IsLyricsViewActive)
        {
            IsLyricsPanelOpen = false;
        }

        if (newValue is not AlbumDetailViewModel)
            _albumDetailBackButtonText = null;

        RefreshBackButton();

        // Dispose transient ViewModels (e.g., AlbumDetailViewModel) to release
        // event handlers on singleton services and unmanaged resources like Bitmaps.
        // BUT: skip disposal if the old view is being kept in navigation history
        // for back-navigation (e.g., album detail → artist discography → back).
        // Also skip cached singletons — disposing them kills long-lived event
        // subscriptions (e.g., Songs VM stops receiving LibraryUpdated).
        DisposeViewIfTransient(oldValue);
    }

    private void RefreshBackButton()
    {
        TopBar.SearchWatermark = CurrentView is PlaylistFeaturedArtistsViewModel
            ? "Find in Featured Artists"
            : $"Find in {TopBar.CurrentTabName}";

        if (CurrentView is MoreByArtistViewModel mbaVm)
        {
            TopBar.ShowBackButton("Back", GoBackInHistoryCommand, mbaVm.Title);
            return;
        }

        if (CurrentView is PlaylistFeaturedArtistsViewModel featuredArtistsVm)
        {
            TopBar.ShowBackButton("Back to Playlist", GoBackInHistoryCommand, featuredArtistsVm.Title);
            return;
        }

        if (CurrentView is AlbumDetailViewModel)
        {
            TopBar.ShowAlbumDetailBackButton(GetCurrentAlbumDetailBackButtonText(), GoBackInHistoryCommand);
            return;
        }

        if (ReferenceEquals(CurrentView, _albumsVm) && _albumsVm.IsArtistFiltered)
        {
            TopBar.ShowBackButton("Back to Albums", GoBackInHistoryCommand, _albumsVm.HeaderText);
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
            TopBar.ShowBackButton("Back to Playlists", new RelayCommand(() => Navigate("playlists")));
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

        return _navigationHistory.Any(entry => ReferenceEquals(entry.View, view));
    }

    private void ClearNavigationHistory()
    {
        while (_navigationHistory.Count > 0)
        {
            var entry = _navigationHistory.Pop();
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
        _navigationHistory.Push(CaptureCurrentNavigationEntry());
    }

    [RelayCommand]
    private void GoBackInHistory()
    {
        if (_navigationHistory.Count == 0)
            return;

        var target = _navigationHistory.Pop();
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
            return "Back to Albums";
        if (view is PlaylistViewModel)
            return "Back to Playlists";
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
        return key switch
        {
            "home" => "Back to Home",
            "songs" => "Back to Songs",
            "albums" => "Back to Albums",
            "artists" => "Back to Artists",
            "folders" => "Back to Folders",
            "playlists" => "Back to Playlists",
            "favorites" => "Back to Favorites",
            "queue" => "Back to Queue",
            "lyrics" => "Back to Lyrics",
            "statistics" => "Back to Statistics",
            "settings" => "Back to Settings",
            { } playlistKey when playlistKey.StartsWith("playlist:", StringComparison.Ordinal) => "Back to Playlists",
            _ => "Back to Albums"
        };
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

        if ((string.IsNullOrWhiteSpace(text) || text == "Back" || text == "Back to Albums")
            && !ReferenceEquals(entry.View, _albumsVm)
            && !ReferenceEquals(entry.View, _coverFlowVm)
            && entry.View is not AlbumDetailViewModel)
        {
            text = GetSectionBackButtonText(sourceSectionKey);
        }
        else if (text == "Back to Albums" && sourceSectionKey is not "albums")
        {
            text = GetSectionBackButtonText(sourceSectionKey);
        }

        return NormalizeAlbumDetailBackButtonText(text);
    }

    private static string NormalizeAlbumDetailBackButtonText(string? text)
    {
        // Empty/null falls back to "Back to Albums". A literal "Back" is intentional
        // (e.g. when the previous view is an artist discography that lives on _albumsVm
        // but isn't the Albums grid) — leave it as-is.
        return string.IsNullOrWhiteSpace(text) ? "Back to Albums" : text;
    }

    private bool IsLongLivedView(ViewModelBase? view)
    {
        return ReferenceEquals(view, _homeVm)
               || ReferenceEquals(view, _songsVm)
               || ReferenceEquals(view, _albumsVm)
               || ReferenceEquals(view, _artistsVm)
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
        Navigate(key);
    }

    [RelayCommand]
    private void Navigate(string key)
    {
        DebugLogger.Info(DebugLogger.Category.UI, "Navigate", $"key={key}, from={GetCurrentViewKey()}, coverFlow={_isCoverFlowMode}");
        ClearNavigationHistory();

        var goingToEligibleSection = ToggleEligibleSections.Contains(key);

        // If we're in Cover Flow and the user clicks an ineligible destination
        // (album detail, settings, lyrics, etc.), auto-exit Cover Flow first
        // so the toggle hide and the destination view land in a consistent state.
        if (_isCoverFlowMode && !goingToEligibleSection)
        {
            _isCoverFlowMode = false;
            TopBar.IsCoverFlowMode = false;
            TopBar.IsSearchVisible = _currentSectionKey != "home";
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

        // Close queue popup and clear search when switching views
        Player.IsQueuePopupOpen = false;
        TopBar.SearchText = string.Empty;

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
        if (goingToEligibleSection)
            SetupGlobalViewModeToggle();
        if (key == "songs")
            SetupSongsTopBarActions();
        else if (key == "playlists")
            TopBar.ShowPlaylistActions(_playlistsVm.CreateSmartPlaylistCommand);
        else if (key == "favorites")
            TopBar.ShowFavoritesActions(_favoritesVm.ShuffleAllCommand, _favoritesVm.PlayAllCommand);

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


    private void OnPlaylistOpened(object? sender, Playlist playlist)
    {
        PushCurrentViewToHistory();
        ClearAllTopBarActions();

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

    private void OpenAlbumDetail(Album album, bool pushHistory = true, string? backButtonText = null)
    {
        if (pushHistory)
            PushCurrentViewToHistory();
        _albumDetailBackButtonText = NormalizeAlbumDetailBackButtonText(backButtonText ?? GetAlbumDetailBackButtonText());
        ClearAllTopBarActions();

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
            _songsVm.SetShowAllItemsCommand,
            _songsVm.SetShowOnlyFavoritesCommand,
            _songsVm.SortCommand);
        _songsVmTopBarHandler = (_, e) =>
        {
            if (e.PropertyName == nameof(LibrarySongsViewModel.ShowOnlyFavorites))
                TopBar.PageShowOnlyFavorites = _songsVm.ShowOnlyFavorites;
            else if (e.PropertyName == nameof(LibrarySongsViewModel.SortAscending))
                TopBar.PageSortAscending = _songsVm.SortAscending;
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
    }

    /// <summary>Clears all page-specific, playlist, artist, and view-mode top bar actions.</summary>
    private void ClearAllTopBarActions()
    {
        ClearTopBarPageActions();
        TopBar.HidePlaylistActions();
        TopBar.HideArtistActions();
        TopBar.HideFavoritesActions();
        TopBar.HideViewModeToggle();
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
            _isCoverFlowMode);

        // Home and Cover Flow do not use the top-bar search field.
        if (_isCoverFlowMode || _currentSectionKey == "home")
            TopBar.IsSearchVisible = false;
    }

    private void EnterCoverFlowMode()
    {
        if (_isCoverFlowMode) return;
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
        // Return to the section that was selected underneath
        CurrentView = ResolveSectionView(_currentSectionKey);
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
            TopBar.ShowPlaylistActions(_playlistsVm.CreateSmartPlaylistCommand);
        else if (ReferenceEquals(view, _favoritesVm))
            TopBar.ShowFavoritesActions(_favoritesVm.ShuffleAllCommand, _favoritesVm.PlayAllCommand);
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
        OpenArtistDiscography(artistName);
    }

    private void ViewArtistFromAlbumDetail(string artistName)
    {
        OpenArtistDiscography(artistName);
    }

    /// <summary>Navigates to the artist-filtered albums view from any track/artist link click.</summary>
    private void ViewArtistByName(string artistName)
    {
        OpenArtistDiscography(artistName);
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
            // Set lyrics directly — don't call Navigate() which would ClearNavigationHistory()
            EnsureLyricsAndReturn(_lyricsVm);
            CurrentView = _lyricsVm;
            Player.IsQueuePopupOpen = false;
            TopBar.SearchText = string.Empty;
            TopBar.CurrentTabName = "Lyrics";
            ClearAllTopBarActions();
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
        if (_scrobbleTrack == null || !_lastFm.IsAuthenticated || !Settings.LastFmScrobblingEnabled)
            return;

        var elapsed = DateTime.UtcNow - _trackStartedAt;
        var duration = _scrobbleTrack.Duration;

        // Last.fm scrobble rules: played > 50% of duration OR > 4 minutes
        bool shouldScrobble = duration.TotalSeconds > 0
            && (elapsed.TotalSeconds > duration.TotalSeconds * 0.5 || elapsed.TotalMinutes > 4);

        if (shouldScrobble)
        {
            var track = _scrobbleTrack;
            var startedAt = _trackStartedAt;
            _ = _lastFm.ScrobbleAsync(track, startedAt);
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


