using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// ViewModel for the Home tab — shows top songs by play count and recently played albums.
/// </summary>
public partial class HomeViewModel : ViewModelBase, IDisposable
{
    private const int MaxTopArtists = 6;
    private readonly PlayerViewModel _player;
    private readonly ILibraryService _library;
    private readonly SidebarViewModel _sidebar;
    private readonly ArtistImageService? _artistImages;
    private readonly IPlayHistoryService? _playHistory;
    private readonly SettingsViewModel? _settings;
    private bool _adoptingPersistedState;
    private readonly DispatcherTimer _refreshDebounce;
    private readonly EventHandler _libraryUpdatedHandler;
    private readonly EventHandler _favoritesChangedHandler;
    private bool _isDirty = true;

    /// <summary>Saved scroll offset for restoring position after navigation.</summary>
    public double SavedScrollOffset { get; set; }

    /// <summary>Albums currently Ctrl-selected in the view. Set by code-behind.</summary>
    public List<Album> CtrlSelectedAlbums { get; set; } = new();

    /// <summary>The Ctrl-selection when the acted-on album is part of it (or none was given), else just that album.</summary>
    private List<Album> SelectionOr(Album? album) =>
        album == null || CtrlSelectedAlbums.Contains(album) ? CtrlSelectedAlbums.ToList() : new List<Album> { album };

    /// <summary>Top songs sorted by play count descending.</summary>
    public BulkObservableCollection<Track> TopSongs { get; } = new();

    /// <summary>Ranked display rows for TopSongs (rank numeral + play-count bar).</summary>
    public BulkObservableCollection<TopSongRow> TopSongRows { get; } = new();

    /// <summary>Recently played albums (grouped from playback history).</summary>
    public BulkObservableCollection<Album> RecentlyPlayedAlbums { get; } = new();

    // ── Albums rail ──
    //
    // The Home page's right-hand column: the album that played most recently as a
    // big card with its track list.
    private const int MaxRailTracks = 12;

    /// <summary>Album featured in the rail's big card (null when nothing was played yet).</summary>
    [ObservableProperty] private Album? _recentRailAlbum;

    /// <summary>Leading tracks of <see cref="RecentRailAlbum"/> in disc/track order.</summary>
    public BulkObservableCollection<Track> RecentRailTracks { get; } = new();

    // ── Last Played chart ──
    //
    // The six most recent distinct tracks from the play history, in the same row
    // template as Most Played. Lives under the hero block where Heavy rotation was.
    private const int MaxLastPlayed = 6;

    /// <summary>Most recent distinct tracks, newest first (the queue a row click plays).</summary>
    public BulkObservableCollection<Track> LastPlayed { get; } = new();

    /// <summary>Numbered display rows for <see cref="LastPlayed"/>.</summary>
    public BulkObservableCollection<TopSongRow> LastPlayedRows { get; } = new();

    /// <summary>Top artists by total play count across all their tracks.</summary>
    public BulkObservableCollection<Artist> TopArtists { get; } = new();

    // ── Continue listening hero ──
    //
    // The first thing on the page (09-13 Home design 1): the track you left off on, its
    // album, and how much of it is left. The player's loaded track wins because it is
    // what Resume acts on; otherwise the newest history entry.
    private const int MaxRecentAlbums = 6;

    [ObservableProperty] private Track? _continueTrack;
    [ObservableProperty] private Album? _continueAlbum;
    /// <summary>" · Album · Year" after the accent-coloured artist name ("" when neither is known).</summary>
    [ObservableProperty] private string _continueDetail = string.Empty;
    /// <summary>The hero track is the loaded track and it is playing: the button reads Pause.</summary>
    [ObservableProperty] private bool _isContinuePlaying;

    // ── Albums row tile size ──
    //
    // Same maths as the Albums page (AlbumGridMetrics): five covers across in Auto,
    // otherwise the column count nearest the cover-size slider, so the Home covers are
    // the size the user already chose for the grid. The view feeds its usable width.
    [ObservableProperty] private double _albumTileSize = 220;
    private double _lastAlbumUsableWidth;

    public void UpdateAlbumTileSize(double usableWidth)
    {
        if (!double.IsFinite(usableWidth) || usableWidth <= 0) return;
        _lastAlbumUsableWidth = usableWidth;
        var auto = _settings?.AlbumTileSizeAuto ?? true;
        var target = _settings?.AlbumTileTargetSize ?? 220;
        var columns = AlbumGridMetrics.ComputeColumns(usableWidth, auto, target);
        var size = AlbumGridMetrics.ComputeTileSize(usableWidth, columns);
        if (Math.Abs(size - AlbumTileSize) >= 0.5) AlbumTileSize = size;
    }

    private void UpdateContinue()
    {
        var track = _player.CurrentTrack ?? LastPlayed.FirstOrDefault();
        ContinueTrack = track;
        if (track == null)
        {
            ContinueAlbum = null;
            ContinueDetail = string.Empty;
            IsContinuePlaying = false;
            return;
        }

        ContinueAlbum = track.AlbumId != Guid.Empty ? _library.GetAlbumById(track.AlbumId) : null;
        ContinueDetail = BuildContinueDetail(track);
        IsContinuePlaying = ReferenceEquals(_player.CurrentTrack, track) && _player.State == PlaybackState.Playing;
    }

    /// <summary>" · Album · Year" — the part after the artist name. Album and year are each
    /// skipped when unknown; the leading separator only appears when something follows the
    /// artist. Internal for tests.</summary>
    internal static string BuildContinueDetail(Track track)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(track.Album)) parts.Add(track.Album);
        if (track.DisplayYear > 0) parts.Add(track.DisplayYear.ToString());
        return parts.Count == 0 ? string.Empty : " · " + string.Join(" · ", parts);
    }

    private void OnPlayerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.CurrentTrack) or nameof(PlayerViewModel.State))
            UpdateContinue();
    }

    /// <summary>Hero button. For the loaded track it is the same toggle as the player bar's
    /// play/pause (so the two stay in step); otherwise it plays the hero track from the Last
    /// Played list (alone if it is no longer in it).</summary>
    [RelayCommand]
    private void ResumeContinue()
    {
        var track = ContinueTrack;
        if (track == null) return;
        if (ReferenceEquals(_player.CurrentTrack, track))
        {
            _player.PlayPauseCommand.Execute(null);
            return;
        }
        if (LastPlayed.Contains(track)) PlayFromRow(LastPlayed, track);
        else _player.ReplaceQueueAndPlay(new List<Track> { track }, 0);
    }

    /// <summary>Hero "Play album": the whole album from track 1 in disc/track order, not
    /// from whichever track the album's list happens to start with.</summary>
    [RelayCommand]
    private void PlayContinueAlbum()
    {
        var album = ContinueAlbum;
        if (album?.Tracks == null || album.Tracks.Count == 0) return;
        var ordered = album.Tracks
            .OrderBy(t => t.DiscNumber <= 0 ? 1 : t.DiscNumber)
            .ThenBy(t => t.TrackNumber)
            .ToList();
        _player.ReplaceQueueAndPlay(ordered, 0);
    }

    // ── Time-aware rows (local play history) ──

    /// <summary>Tracks the user keeps playing around this time of day.</summary>
    public BulkObservableCollection<Track> TimeRotationTracks { get; } = new();

    /// <summary>Most-played tracks of the last two weeks.</summary>
    public BulkObservableCollection<Track> HeavyRotationTracks { get; } = new();

    /// <summary>Tracks recently played again after a long break.</summary>
    public BulkObservableCollection<Track> RediscoveredTracks { get; } = new();

    /// <summary>Title of the time-of-day row ("Morning rotation" etc.).</summary>
    [ObservableProperty] private string _timeRotationTitle = HomeRowsBuilder.DaypartLabel(DateTime.Now.Hour);

    [ObservableProperty] private string _greeting = GetGreeting();

    // ── Section collapse ──
    //
    // Each Home section can be folded away to its header so the page shows only what
    // the user currently wants (requested by Luwi: "too much visual input"). The state
    // round-trips through SettingsViewModel — the same route Songs/Albums view state
    // takes — so a fold survives a restart. All start expanded, the pre-feature layout.

    [ObservableProperty] private bool _isTopSongsExpanded = true;
    [ObservableProperty] private bool _isTopArtistsExpanded = true;
    [ObservableProperty] private bool _isRecentlyPlayedExpanded = true;
    [ObservableProperty] private bool _isTimeRotationExpanded = true;
    [ObservableProperty] private bool _isHeavyRotationExpanded = true;
    [ObservableProperty] private bool _isRediscoveredExpanded = true;
    [ObservableProperty] private bool _isLastPlayedExpanded = true;

    /// <summary>
    /// Heavy rotation shows only while it has rows AND the Appearance toggle is on.
    /// Re-raised when either input moves (collection reset, settings change).
    /// </summary>
    public bool IsHeavyRotationVisible => HeavyRotationTracks.Count > 0 && (_settings?.HomeShowHeavyRotation ?? true);

    /// <summary>Fires when the user wants to open an album's detail view.</summary>
    public event EventHandler<Album>? AlbumOpened;

    /// <summary>Exposes the sidebar's playlists for the Add to Playlist submenu.</summary>
    public ObservableCollection<Playlist> Playlists => _sidebar.Playlists;

    public HomeViewModel(PlayerViewModel player, ILibraryService library, SidebarViewModel sidebar,
        ArtistImageService? artistImages = null, IPlayHistoryService? playHistory = null,
        SettingsViewModel? settings = null)
    {
        _player = player;
        _library = library;
        _sidebar = sidebar;
        _artistImages = artistImages;
        _playHistory = playHistory;
        _settings = settings;

        AdoptPersistedSectionState();

        HeavyRotationTracks.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsHeavyRotationVisible));
        if (_settings != null)
        {
            _settings.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SettingsViewModel.HomeShowHeavyRotation))
                    OnPropertyChanged(nameof(IsHeavyRotationVisible));
                else if (e.PropertyName is nameof(SettingsViewModel.AlbumTileSizeAuto) or nameof(SettingsViewModel.AlbumTileTargetSize))
                    UpdateAlbumTileSize(_lastAlbumUsableWidth);
            };
        }

        // Subscribe to track changes for real-time updates
        _player.TrackStarted += OnTrackStarted;
        _player.PropertyChanged += OnPlayerPropertyChanged;

        // Subscribe to library changes with debounce to avoid flooding UI thread
        _refreshDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _refreshDebounce.Tick += (_, _) =>
        {
            _refreshDebounce.Stop();
            Refresh();
        };
        // Rebuild only while Home is the current view: hidden views just mark dirty
        // and catch up once on activation, like the other library view models.
        // FavoritesChanged rides the same 500 ms debounce — a heart click used to
        // trigger an immediate full rebuild.
        _libraryUpdatedHandler = (_, _) => { _isDirty = true; if (_isActive) Dispatcher.UIThread.Post(() =>
        {
            _refreshDebounce.Stop();
            _refreshDebounce.Start();
        }); };
        _favoritesChangedHandler = (_, _) => { _isDirty = true; if (_isActive) Dispatcher.UIThread.Post(() =>
        {
            _refreshDebounce.Stop();
            _refreshDebounce.Start();
        }); };
        _library.LibraryUpdated += _libraryUpdatedHandler;
        _library.FavoritesChanged += _favoritesChangedHandler;
    }

    /// <summary>Applies the fold state persisted from the previous session.</summary>
    private void AdoptPersistedSectionState()
    {
        if (_settings == null) return;

        // The guard keeps the adopt pass from writing the values straight back out —
        // harmless here, but it would queue a settings save on every startup.
        _adoptingPersistedState = true;
        try
        {
            IsTopSongsExpanded = _settings.HomeTopSongsExpanded;
            IsTopArtistsExpanded = _settings.HomeTopArtistsExpanded;
            IsRecentlyPlayedExpanded = _settings.HomeRecentlyPlayedExpanded;
            IsTimeRotationExpanded = _settings.HomeTimeRotationExpanded;
            IsHeavyRotationExpanded = _settings.HomeHeavyRotationExpanded;
            IsRediscoveredExpanded = _settings.HomeRediscoveredExpanded;
            IsLastPlayedExpanded = _settings.HomeLastPlayedExpanded;
        }
        finally
        {
            _adoptingPersistedState = false;
        }
    }

    partial void OnIsTopSongsExpandedChanged(bool value)
    {
        if (_settings != null && !_adoptingPersistedState) _settings.HomeTopSongsExpanded = value;
    }

    partial void OnIsTopArtistsExpandedChanged(bool value)
    {
        if (_settings != null && !_adoptingPersistedState) _settings.HomeTopArtistsExpanded = value;
    }

    partial void OnIsRecentlyPlayedExpandedChanged(bool value)
    {
        if (_settings != null && !_adoptingPersistedState) _settings.HomeRecentlyPlayedExpanded = value;
    }

    partial void OnIsTimeRotationExpandedChanged(bool value)
    {
        if (_settings != null && !_adoptingPersistedState) _settings.HomeTimeRotationExpanded = value;
    }

    partial void OnIsHeavyRotationExpandedChanged(bool value)
    {
        if (_settings != null && !_adoptingPersistedState) _settings.HomeHeavyRotationExpanded = value;
    }

    partial void OnIsRediscoveredExpandedChanged(bool value)
    {
        if (_settings != null && !_adoptingPersistedState) _settings.HomeRediscoveredExpanded = value;
    }

    partial void OnIsLastPlayedExpandedChanged(bool value)
    {
        if (_settings != null && !_adoptingPersistedState) _settings.HomeLastPlayedExpanded = value;
    }

    /// <summary>
    /// Folds one Home section open or shut. Keyed by name rather than one command per
    /// section so the header template stays a single reusable button.
    /// </summary>
    [RelayCommand]
    private void ToggleSection(string? section)
    {
        switch (section)
        {
            case "TopSongs": IsTopSongsExpanded = !IsTopSongsExpanded; break;
            case "TopArtists": IsTopArtistsExpanded = !IsTopArtistsExpanded; break;
            case "RecentlyPlayed": IsRecentlyPlayedExpanded = !IsRecentlyPlayedExpanded; break;
            case "TimeRotation": IsTimeRotationExpanded = !IsTimeRotationExpanded; break;
            case "HeavyRotation": IsHeavyRotationExpanded = !IsHeavyRotationExpanded; break;
            case "Rediscovered": IsRediscoveredExpanded = !IsRediscoveredExpanded; break;
            case "LastPlayed": IsLastPlayedExpanded = !IsLastPlayedExpanded; break;
        }
    }

    private void OnTrackStarted(object? sender, Track track)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Update greeting in case time changed
            Greeting = GetGreeting();

            var album = _library.GetAlbumById(track.AlbumId);
            if (album != null)
            {
                var existing = RecentlyPlayedAlbums.FirstOrDefault(a => a.Id == album.Id);
                if (existing != null)
                    RecentlyPlayedAlbums.Remove(existing);

                RecentlyPlayedAlbums.Insert(0, album);

                while (RecentlyPlayedAlbums.Count > 10)
                    RecentlyPlayedAlbums.RemoveAt(RecentlyPlayedAlbums.Count - 1);

                RebuildRecentRail();
            }

            var recentTracks = LastPlayed.Where(t => t.Id != track.Id).ToList();
            recentTracks.Insert(0, track);
            ReplaceLastPlayed(recentTracks);
        });
    }

    /// <summary>
    /// Set by MainWindowViewModel when Home becomes (or stops being) the current view.
    /// Mirrors LibrarySongsViewModel.IsActive: gates event-driven rebuilds while
    /// hidden, and catches up on activation (no-op when nothing changed).
    /// </summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            // Covers back-navigation paths that swap CurrentView without a Refresh call.
            if (value) Refresh();
        }
    }

    // True at construction: Home is the startup view, and UpdateSectionActiveFlags
    // only runs on the first navigation/modal change.
    private bool _isActive = true;

    /// <summary>Forces the next Refresh() call to rebuild even if data hasn't changed.</summary>
    public void MarkDirty() => _isDirty = true;

    /// <summary>Refreshes the Home tab content with latest data.</summary>
    public void Refresh() => _ = RefreshAsync();

    internal async Task RefreshAsync()
    {
        if (!_isDirty && TopSongs.Count > 0)
        {
            Greeting = GetGreeting();
            UpdateContinue();
            // Time-aware rows depend on the clock and the play log, both of which
            // move without dirtying the library — rebuild them on every visit.
            _ = RefreshTimeAwareRowsAsync();
            return;
        }
        _isDirty = false;

        try
        {
            Greeting = GetGreeting();

            // Top songs: tracks with highest play count
            var allTracks = _library.Tracks;
            if (allTracks.Count > 0)
            {
                var top = await Task.Run(() =>
                    allTracks
                        .Where(t => t.PlayCount > 0)
                        .OrderByDescending(t => t.PlayCount)
                        .Take(6)
                        .ToList());
                ReplaceTopSongsIfChanged(top);
            }
            else
            {
                ReplaceTopSongsIfChanged(Array.Empty<Track>());
            }

            // Recently played albums + Last Played read the PERSISTED play log (09-14):
            // Player.History is a transport list — StopAndClear wipes it when a queue
            // plays to its end, and the shutdown snapshot then saves an empty history,
            // so both rows came back with only the current track after a restart.
            var history = RecentHistoryNewestFirst();
            // O(1) lookups via GetAlbumById
            var recentAlbums = history
                .Take(50)
                .Select(t => t.AlbumId)
                .Distinct()
                .Take(MaxRecentAlbums)
                .Select(id => _library.GetAlbumById(id))
                .OfType<Album>()
                .ToList();
            // Only reset when the row actually changed: a Reset tears every tile down and
            // Avalonia closes a ContextMenu whose owner leaves the tree, so the 500 ms
            // refresh after a menu option (Favorites…) used to snap a re-opened menu shut.
            if (!SameSequence(RecentlyPlayedAlbums, recentAlbums))
                RecentlyPlayedAlbums.ReplaceAll(recentAlbums);
            RebuildRecentRail();
            ReplaceLastPlayed(history);
            UpdateContinue();

            // Top Artists: aggregate play count by artist name (using album-artist
            // grouping that the library already maintains), drop the "Unknown Artist"
            // sentinel, and keep the top 6 for the home row.
            var topArtists = await Task.Run(() =>
            {
                var plays = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in allTracks)
                {
                    if (t.PlayCount <= 0) continue;
                    var name = t.GroupingArtist;
                    if (string.IsNullOrWhiteSpace(name) ||
                        string.Equals(name, "Unknown Artist", StringComparison.OrdinalIgnoreCase))
                        continue;
                    plays.TryGetValue(name, out var c);
                    plays[name] = c + t.PlayCount;
                }

                if (plays.Count == 0) return new List<Artist>();

                // Resolve the play-count ranking back to actual Artist rows so the row
                // benefits from the existing image-cache flow.
                var byName = _library.Artists.ToDictionary(a => a.Name, a => a, StringComparer.OrdinalIgnoreCase);
                return plays
                    .OrderByDescending(kv => kv.Value)
                    .Take(MaxTopArtists)
                    .Select(kv => byName.TryGetValue(kv.Key, out var artist)
                        ? artist
                        : new Artist { Name = kv.Key, Id = ComputeArtistId(kv.Key) })
                    .ToList();
            });
            ReplaceTopArtistsIfChanged(topArtists);

            // Kick a background image-fetch for any artist that doesn't have a cached
            // picture yet, then re-materialize the row so the portraits are actually read.
            if (_artistImages != null && topArtists.Count > 0)
                _ = _artistImages.FetchAndCacheAsync(topArtists, (_, _) =>
                    Dispatcher.UIThread.Post(ScheduleTopArtistImageRefresh));

            await RefreshTimeAwareRowsAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HomeVM] Refresh failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Rebuilds the three play-history rows (time-of-day, heavy rotation,
    /// rediscovered). Ranking runs off the UI thread; track resolution drops
    /// IDs that no longer exist in the library.
    /// </summary>
    private async Task RefreshTimeAwareRowsAsync()
    {
        if (_playHistory == null) return;

        var now = DateTime.Now;
        TimeRotationTitle = HomeRowsBuilder.DaypartLabel(now.Hour);

        var events = _playHistory.Events;

        // Dedupe rows against Most Listened To and each other so every row
        // shows tracks the user hasn't already seen further up the page.
        var exclude = TopSongs.Select(t => t.Id).ToHashSet();
        var (timeIds, heavyIds, rediscoveredIds) = await Task.Run(() =>
        {
            var heavy = HomeRowsBuilder.BuildHeavyRotation(events, now, exclude: exclude);
            exclude.UnionWith(heavy);
            var time = HomeRowsBuilder.BuildTimeOfDayRotation(events, now, exclude: exclude);
            exclude.UnionWith(time);
            var rediscovered = HomeRowsBuilder.BuildRediscovered(events, now, exclude: exclude);
            return (time, heavy, rediscovered);
        });

        // Hide a row entirely when it has too few tracks to earn its header.
        List<Track> Resolve(List<Guid> ids)
        {
            var tracks = ids.Select(_library.GetTrackById).OfType<Track>().ToList();
            return tracks.Count >= HomeRowsBuilder.MinRowItems ? tracks : new List<Track>();
        }

        // These rows rebuild on every Home visit (clock + play log move without the
        // library dirtying), so skip the Reset when the resolved tracks are unchanged —
        // each Reset re-materializes every container in the row, felt as a stutter on
        // each visit on slow renderers (issue #31).
        ReplaceRowIfChanged(TimeRotationTracks, Resolve(timeIds));
        ReplaceRowIfChanged(HeavyRotationTracks, Resolve(heavyIds));
        ReplaceRowIfChanged(RediscoveredTracks, Resolve(rediscoveredIds));
    }

    /// <summary>
    /// Replaces the row only when membership or order actually changed. Reference
    /// equality on purpose: a rescan rebuilds Track instances with the same Id but
    /// fresh metadata, and an Id-based skip would leave the row bound to stale
    /// objects. Internal for tests (InternalsVisibleTo Noctis.Tests).
    /// </summary>
    internal static void ReplaceRowIfChanged(BulkObservableCollection<Track> row, IReadOnlyList<Track> next)
    {
        if (row.Count == next.Count)
        {
            var same = true;
            for (int i = 0; i < next.Count; i++)
            {
                if (!ReferenceEquals(row[i], next[i])) { same = false; break; }
            }
            if (same) return;
        }

        row.ReplaceAll(next);
    }

    /// <summary>Plays a track from one of the time-aware rows, queueing the rest of its row.</summary>
    [RelayCommand]
    private void PlayTimeRotation(Track track) => PlayFromRow(TimeRotationTracks, track);

    [RelayCommand]
    private void PlayHeavyRotation(Track track) => PlayFromRow(HeavyRotationTracks, track);

    [RelayCommand]
    private void PlayRediscovered(Track track) => PlayFromRow(RediscoveredTracks, track);

    private void PlayFromRow(BulkObservableCollection<Track> row, Track track)
    {
        var tracks = row.ToList();
        var index = tracks.IndexOf(track);
        if (index < 0) index = 0;
        if (tracks.Count == 0) return;
        _player.ReplaceQueueAndPlay(tracks, index);
    }

    private static List<TopSongRow> BuildTopSongRows(IReadOnlyList<Track> top)
        => top.Select((t, i) => new TopSongRow { Track = t, Rank = i + 1 }).ToList();

    /// <summary>
    /// Rebuilds the Most Played rows only when the ranking actually moved. A favorite
    /// toggle dirties Home and refreshes it with the very same top tracks; rebuilding
    /// the rows then re-realizes every row control, which detaches the one a context
    /// menu was just opened on, and Avalonia shuts a menu whose owner leaves the tree
    /// (the "opens and closes" glitch). Hearts and counts are bound per track, so
    /// untouched rows stay live.
    /// </summary>
    private static bool SameSequence<T>(IReadOnlyList<T> current, IReadOnlyList<T> next) where T : class
        => current.Count == next.Count && current.Zip(next).All(p => ReferenceEquals(p.First, p.Second));

    private void ReplaceTopSongsIfChanged(IReadOnlyList<Track> top)
    {
        if (SameSequence(TopSongs, top))
            return;
        TopSongs.ReplaceAll(top);
        TopSongRows.ReplaceAll(BuildTopSongRows(top));
    }

    private static string GetGreeting()
    {
        var hour = DateTime.Now.Hour;
        return hour switch
        {
            >= 5 and < 12 => "Good morning",
            >= 12 and < 17 => "Good afternoon",
            >= 17 and < 21 => "Good evening",
            _ => "Good night"
        };
    }

    private DispatcherTimer? _topArtistImageDebounce;

    /// <summary>
    /// Rebuilds the Top Artists items once portrait fetches land.
    ///
    /// The fetch callback used to raise PropertyChanged for the TopArtists *property*,
    /// which does nothing useful: it is the same collection instance and carries no
    /// CollectionChanged, so the item containers are never rebuilt — and Artist.ImagePath
    /// is a plain property that raises nothing of its own, so no binding ever re-read it.
    /// The row kept its placeholder silhouettes for the entire session even with every
    /// portrait already sitting in the cache. ReplaceAll fires Reset, which re-materializes
    /// the items and re-reads the path, exactly as the Artists tab already does.
    ///
    /// Debounced because the fetch reports once per artist.
    /// </summary>
    private void ScheduleTopArtistImageRefresh()
    {
        if (_topArtistImageDebounce == null)
        {
            _topArtistImageDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _topArtistImageDebounce.Tick += (_, _) =>
            {
                _topArtistImageDebounce!.Stop();
                if (TopArtists.Count > 0)
                    TopArtists.ReplaceAll(TopArtists.ToList());
            };
        }

        _topArtistImageDebounce.Stop();
        _topArtistImageDebounce.Start();
    }

    private void ReplaceTopArtistsIfChanged(IReadOnlyList<Artist> artists)
    {
        if (TopArtists.Count == artists.Count &&
            TopArtists.Zip(artists).All(pair => pair.First.Id == pair.Second.Id))
            return;

        TopArtists.ReplaceAll(artists);
    }

    private static Guid ComputeArtistId(string artistName)
    {
        var hash = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(artistName.Trim().ToLowerInvariant()));
        return new Guid(hash);
    }

    // ── Top Song commands ──

    [RelayCommand]
    private void PlayTopSong(Track track)
    {
        var tracks = TopSongs.ToList();
        var index = tracks.IndexOf(track);
        if (index < 0) index = 0;
        _player.ReplaceQueueAndPlay(tracks, index);
    }

    [RelayCommand]
    private void PlayNext(Track track) => _player.AddNext(track);

    [RelayCommand]
    private void AddToQueue(Track track) => _player.AddToQueue(track);

    [RelayCommand]
    private async Task AddTrackToNewPlaylist(Track track)
    {
        await _sidebar.CreatePlaylistWithTrackAsync(track);
    }

    [RelayCommand]
    private async Task AddTrackToExistingPlaylist(object[] parameters)
    {
        if (parameters == null || parameters.Length != 2) return;
        if (parameters[0] is not Track track || parameters[1] is not Playlist playlist) return;
        await _sidebar.AddTracksToPlaylist(playlist.Id, new[] { track });
    }

    [RelayCommand]
    private async Task ToggleTrackFavorite(Track track)
    {
        track.IsFavorite = !track.IsFavorite;
        await _library.SaveTrackUserStateAsync(new[] { track });
        _library.NotifyFavoritesChanged(new[] { track });
    }

    [RelayCommand]
    private void ShowInExplorerTrack(Track track)
    {
        if (track == null || !File.Exists(track.FilePath)) return;
        Helpers.PlatformHelper.ShowInFileManager(track.FilePath);
    }

    [RelayCommand]
    private async Task OpenTrackMetadata(Track track)
    {
        await MetadataHelper.OpenMetadataWindow(track);
    }

    [RelayCommand]
    private async Task ConvertTrack(Track track)
        => await MetadataHelper.OpenAudioConverterDialog(new List<Track> { track });

    [RelayCommand]
    private async Task ScanTrackReplayGain(Track track)
        => await MetadataHelper.OpenReplayGainScannerDialog(new List<Track> { track });

    [RelayCommand]
    private void SearchLyricsTrack(Track track)
    {
        _searchLyricsAction?.Invoke(track);
    }

    [RelayCommand]
    private void ShuffleTopSongs() => ShuffleRow(TopSongs);

    [RelayCommand]
    private void ShuffleTimeRotation() => ShuffleRow(TimeRotationTracks);

    [RelayCommand]
    private void ShuffleHeavyRotation() => ShuffleRow(HeavyRotationTracks);

    [RelayCommand]
    private void ShuffleRediscovered() => ShuffleRow(RediscoveredTracks);

    private void ShuffleRow(IEnumerable<Track> row)
    {
        var tracks = row.ToList();
        if (tracks.Count == 0) return;
        var shuffled = Helpers.ShuffleHelper.WeightedShuffle(tracks);
        _player.ReplaceQueueAndPlay(shuffled, 0);
    }

    [RelayCommand]
    private void StartRadio(Track track) => _player.StartRadioCommand.Execute(track);

    [RelayCommand]
    private void SnoozeForMonth(Track track) => _player.SnoozeForMonthCommand.Execute(track);

    /// <summary>Fires when the user wants to view a track's album.</summary>
    public event EventHandler<Track>? ViewAlbumRequested;

    [RelayCommand]
    private void ViewAlbumFromTrack(Track track)
    {
        ViewAlbumRequested?.Invoke(this, track);
    }

    [RelayCommand]
    private async Task RemoveTrackFromLibrary(Track track)
    {
        if (track == null) return;
        await Helpers.LibraryRemovalHelper.RemoveWithPromptAsync(_library, new List<Track> { track });
    }

    // ── Recently Played rail ──

    internal readonly record struct RecentRail(Album? Featured, List<Track> Tracks);

    /// <summary>The newest recent album and its leading tracks in disc/track order.</summary>
    internal static RecentRail BuildRecentRail(IReadOnlyList<Album> recent, int maxTracks)
    {
        var featured = recent.Count > 0 ? recent[0] : null;
        if (featured == null)
            return new RecentRail(null, new List<Track>());

        var tracks = (featured.Tracks ?? new List<Track>())
            .OrderBy(t => t.DiscNumber <= 0 ? 1 : t.DiscNumber)
            .ThenBy(t => t.TrackNumber)
            .Take(maxTracks)
            .ToList();
        return new RecentRail(featured, tracks);
    }

    private void RebuildRecentRail()
    {
        var rail = BuildRecentRail(RecentlyPlayedAlbums, MaxRailTracks);
        RecentRailAlbum = rail.Featured;
        ReplaceRowIfChanged(RecentRailTracks, rail.Tracks);
    }

    /// <summary>How far back the play log is scanned for the two recent rows.</summary>
    private const int RecentLogScan = 400;

    /// <summary>
    /// Newest-first list of recently started tracks: the persisted play log when one is
    /// wired (survives restarts, queue replacement and the player's 50-item cap), else
    /// the player's in-memory History. Tracks no longer in the library are dropped.
    /// </summary>
    private List<Track> RecentHistoryNewestFirst()
    {
        var events = _playHistory?.Events;
        if (events == null || events.Count == 0)
            return _player.History.ToList();
        // GitHub #86: dropped files played in place are not library tracks; the player
        // still holds them (restored from queue.json), so fall back to its copies.
        Dictionary<Guid, Track>? external = null;
        return BuildRecentFromLog(events,
            id => _library.GetTrackById(id) ?? (external ??= _player.GetExternalTracksById()).GetValueOrDefault(id),
            RecentLogScan);
    }

    /// <summary>
    /// The newest <paramref name="scan"/> log events (oldest-first log) as tracks,
    /// newest first; unresolved ids are skipped. Duplicates are left in — the callers
    /// dedupe by track or album themselves.
    /// </summary>
    internal static List<Track> BuildRecentFromLog(IReadOnlyList<PlayHistoryEvent> events, Func<Guid, Track?> resolve, int scan)
    {
        var result = new List<Track>(Math.Min(scan, events.Count));
        var floor = Math.Max(0, events.Count - scan);
        for (var i = events.Count - 1; i >= floor; i--)
        {
            var track = resolve(events[i].TrackId);
            if (track != null) result.Add(track);
        }
        return result;
    }

    /// <summary>
    /// The most recent distinct tracks from a newest-first history: a track that was
    /// played twice keeps only its newest position.
    /// </summary>
    internal static List<Track> BuildLastPlayed(IEnumerable<Track> historyNewestFirst, int max)
    {
        var seen = new HashSet<Guid>();
        var result = new List<Track>(max);
        foreach (var t in historyNewestFirst)
        {
            if (!seen.Add(t.Id)) continue;
            result.Add(t);
            if (result.Count >= max) break;
        }
        return result;
    }

    private void ReplaceLastPlayed(IEnumerable<Track> historyNewestFirst)
    {
        var next = BuildLastPlayed(historyNewestFirst, MaxLastPlayed);
        if (LastPlayed.Count == next.Count && LastPlayed.Zip(next).All(p => ReferenceEquals(p.First, p.Second)))
            return;
        LastPlayed.ReplaceAll(next);
        LastPlayedRows.ReplaceAll(next.Select((t, i) => new TopSongRow { Track = t, Rank = i + 1, IsLastPlayed = true }).ToList());
    }

    /// <summary>Row click for both charts: queues the row's own list (Most Played or Last Played).</summary>
    [RelayCommand]
    private void PlayChartRow(TopSongRow row)
    {
        if (row.IsLastPlayed) PlayLastPlayed(row.Track);
        else PlayTopSong(row.Track);
    }

    [RelayCommand]
    private void PlayLastPlayed(Track track) => PlayFromRow(LastPlayed, track);

    [RelayCommand]
    private void ShuffleLastPlayed() => ShuffleRow(LastPlayed);

    /// <summary>Plays a track from the rail's track list, queueing the whole featured album.</summary>
    [RelayCommand]
    private void PlayRecentRailTrack(Track track)
    {
        var album = RecentRailAlbum;
        if (album?.Tracks == null || album.Tracks.Count == 0) return;
        var tracks = album.Tracks.ToList();
        var index = tracks.IndexOf(track);
        if (index < 0) index = 0;
        _player.ReplaceQueueAndPlay(tracks, index);
    }

    [RelayCommand]
    private void ShuffleRecentRail(Track _)
    {
        if (RecentRailAlbum != null) ShuffleAlbum(RecentRailAlbum);
    }

    // ── Album commands ──

    [RelayCommand]
    private void OpenAlbum(Album album)
    {
        AlbumOpened?.Invoke(this, album);
    }

    [RelayCommand]
    private void PlayAlbum(Album album)
    {
        if (album == null || album.Tracks == null || album.Tracks.Count == 0) return;
        _player.ReplaceQueueAndPlay(album.Tracks, 0);
    }

    /// <summary>Tile hover button: Pause/resume when this album is the loaded one, else
    /// play it from track 1 in disc/track order.</summary>
    [RelayCommand]
    private void TogglePlayAlbum(Album album)
    {
        if (album == null) return;
        if (album.IsCurrent) { _player.PlayPauseCommand.Execute(null); return; }
        var ordered = Helpers.AlbumTile.OrderedTracks(album);
        if (ordered.Count == 0) return;
        _player.ReplaceQueueAndPlay(ordered, 0);
    }

    [RelayCommand]
    private void ShuffleAlbum(Album album)
    {
        if (album == null || album.Tracks == null || album.Tracks.Count == 0) return;
        var shuffled = Helpers.ShuffleHelper.WeightedShuffle(album.Tracks);
        _player.ReplaceQueueAndPlay(shuffled, 0);
    }

    [RelayCommand]
    private void PlayNextAlbum(Album album)
    {
        if (album == null || album.Tracks == null || album.Tracks.Count == 0) return;

        var tracks = album.Tracks.ToList();
        for (int i = tracks.Count - 1; i >= 0; i--)
        {
            _player.AddNext(tracks[i]);
        }
    }

    [RelayCommand]
    private void AddAlbumToQueue(Album album)
    {
        if (album == null || album.Tracks == null || album.Tracks.Count == 0) return;

        _player.AddRangeToQueue(album.Tracks.ToList());
    }

    [RelayCommand]
    private async Task AddAlbumToNewPlaylist(Album album)
    {
        var albums = SelectionOr(album);
        var tracks = albums.SelectMany(a => a.Tracks ?? new()).ToList();
        if (tracks.Count == 0) return;
        await _sidebar.CreatePlaylistWithTracksAsync(tracks);
        CtrlSelectedAlbums.Clear();
    }

    [RelayCommand]
    private async Task AddAlbumToExistingPlaylist(object[] parameters)
    {
        if (parameters == null || parameters.Length != 2) return;
        if (parameters[0] is not Album album || parameters[1] is not Playlist playlist) return;
        var albums = SelectionOr(album);
        var tracks = albums.SelectMany(a => a.Tracks ?? new()).ToList();
        if (tracks.Count == 0) return;
        await _sidebar.AddTracksToPlaylist(playlist.Id, tracks);
        CtrlSelectedAlbums.Clear();
    }

    [RelayCommand]
    private async Task ToggleAlbumFavorites(Album album)
    {
        var albums = SelectionOr(album);
        if (albums.Count == 0) return;
        var changed = new List<Track>();
        foreach (var a in albums)
        {
            if (a.Tracks == null || a.Tracks.Count == 0) continue;
            var newState = !a.IsAllTracksFavorite;
            foreach (var track in a.Tracks)
            {
                track.IsFavorite = newState;
                changed.Add(track);
            }
        }
        await _library.SaveTrackUserStateAsync(changed);
        _library.NotifyFavoritesChanged(changed);
        CtrlSelectedAlbums.Clear();
    }

    [RelayCommand]
    private async Task OpenMetadata(Album album)
    {
        // Multi-album selection: edit every track across the selected albums in the
        // shared multi-select editor (Mixed fields, edits fan out to all tracks).
        var selection = SelectionOr(album);
        if (selection.Count > 1)
        {
            var tracks = selection.SelectMany(a => a.Tracks ?? new()).ToList();
            CtrlSelectedAlbums.Clear();
            await MetadataHelper.OpenBatchMetadataWindow(tracks);
            return;
        }

        if (album == null || album.Tracks == null || album.Tracks.Count == 0) return;
        await MetadataHelper.OpenMetadataWindow(album.Tracks[0], albumScoped: true);
    }

    [RelayCommand]
    private async Task BatchEditAlbum(Album album)
    {
        if (album?.Tracks == null || album.Tracks.Count == 0) return;
        await MetadataHelper.OpenBatchMetadataWindow(album.Tracks.ToList());
    }

    [RelayCommand]
    private async Task ConvertAlbum(Album album)
    {
        if (album?.Tracks == null || album.Tracks.Count == 0) return;
        await MetadataHelper.OpenAudioConverterDialog(album.Tracks.ToList());
    }

    [RelayCommand]
    private async Task ScanAlbumReplayGain(Album album)
    {
        if (album?.Tracks == null || album.Tracks.Count == 0) return;
        await MetadataHelper.OpenReplayGainScannerDialog(album.Tracks.ToList());
    }

    [RelayCommand]
    private async Task RemoveFromLibrary(Album album)
    {
        var albums = SelectionOr(album);
        if (albums.Count == 0) return;
        var tracks = albums.SelectMany(a => a.Tracks ?? new()).ToList();
        if (!await Helpers.LibraryRemovalHelper.RemoveWithPromptAsync(_library, tracks))
            return;
        CtrlSelectedAlbums.Clear();
    }

    [RelayCommand]
    private void ShowInExplorerAlbum(Album album)
    {
        if (album?.Tracks == null || album.Tracks.Count == 0) return;
        var filePath = album.Tracks[0].FilePath;
        if (!File.Exists(filePath)) return;
        Helpers.PlatformHelper.ShowInFileManager(filePath);
    }

    private Action<Track>? _searchLyricsAction;
    public void SetSearchLyricsAction(Action<Track> action) => _searchLyricsAction = action;

    [RelayCommand]
    private void SearchLyricsAlbum(Album album)
    {
        if (album?.Tracks == null || album.Tracks.Count == 0) return;
        _searchLyricsAction?.Invoke(album.Tracks[0]);
    }

    private Action<string>? _viewArtistAction;
    public void SetViewArtistAction(Action<string> action) => _viewArtistAction = action;

    [RelayCommand]
    private void ViewArtist(string artistName)
    {
        if (!string.IsNullOrWhiteSpace(artistName))
            _viewArtistAction?.Invoke(artistName);
    }

    [RelayCommand]
    private void OpenTopArtist(Artist? artist)
    {
        if (artist == null || string.IsNullOrWhiteSpace(artist.Name)) return;
        _viewArtistAction?.Invoke(artist.Name);
    }

    public void Dispose()
    {
        _refreshDebounce.Stop();
        _topArtistImageDebounce?.Stop();
        _player.TrackStarted -= OnTrackStarted;
        _player.PropertyChanged -= OnPlayerPropertyChanged;
        _library.LibraryUpdated -= _libraryUpdatedHandler;
        _library.FavoritesChanged -= _favoritesChangedHandler;
    }
}
