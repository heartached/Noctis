using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Mobile.Services;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// The phone Library over the shared Core library: the count tiles, the lists they open, the
/// Shelf and the three rails (from the library and the persisted play log), and the folder
/// flow (SAF pick → AppSettings.MusicFolders → scan). Library events arrive on scan threads;
/// <c>marshal</c> hops them to the UI thread (tests pass a direct call). <see cref="Refreshed"/>
/// tells open pages to re-read, so a rescan or a heart toggle shows without re-navigating.
/// </summary>
public sealed partial class LibraryViewModel : ObservableObject
{
    private const int RecentlyAddedDays = 30;
    private const int RailSize = 12;
    private const int ShelfSize = 10;

    private readonly ILibraryService _library;
    private readonly IPersistenceService _persistence;
    private readonly IFolderPicker _picker;
    private readonly IPlayHistoryService? _history;
    private readonly Action<Action> _marshal;
    private List<Playlist> _playlists = new();
    private List<Guid> _pinnedAlbumIds = new();
    private List<string> _pinnedArtistNames = new();
    private List<Guid> _pinnedTrackIds = new();

    public LibraryViewModel(ILibraryService library, IPersistenceService persistence, IFolderPicker picker,
        IPlayHistoryService? history = null, Action<Action>? marshal = null)
    {
        _library = library;
        _persistence = persistence;
        _picker = picker;
        _history = history;
        _marshal = marshal ?? (a => Avalonia.Threading.Dispatcher.UIThread.Post(a));

        _library.LibraryUpdated += (_, _) => _marshal(RefreshFromLibrary);
        _library.FavoritesChanged += (_, _) => _marshal(RefreshFromLibrary);
        _library.ScanProgress += (_, n) => _marshal(() => ScanProgress = n);
        _library.ScanAborted += (_, roots) => _marshal(() =>
        {
            NeedsReconnect = true;
            StatusText = $"{roots.Length} folder(s) unavailable. Reconnect them and rescan.";
        });
    }

    /// <summary>The shared Core library, for the pages built on this view model.</summary>
    public ILibraryService Service => _library;

    public IPersistenceService Persistence => _persistence;

    /// <summary>The persisted play log (null in hosts without one).</summary>
    public IPlayHistoryService? History => _history;

    /// <summary>Raised on the UI thread after every rebuild (library update, favourites, playlists, pins).</summary>
    public event EventHandler? Refreshed;

    [ObservableProperty] private int _songCount;
    [ObservableProperty] private int _albumCount;
    [ObservableProperty] private int _artistCount;
    [ObservableProperty] private int _favoriteCount;
    [ObservableProperty] private int _recentlyAddedCount;
    [ObservableProperty] private int _playlistCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private bool _isScanning;
    [ObservableProperty] private int _scanProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowConnectCard), nameof(HasNotice))]
    private bool _hasFolders;

    /// <summary>The last scan aborted on an unreadable root (a revoked SAF grant, spec §7):
    /// the page offers to re-pick the folder, which is how a dead grant is recovered.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStatusLine), nameof(HasNotice))]
    private bool _needsReconnect;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStatusLine))]
    private string _statusText = string.Empty;

    /// <summary>InitializeAsync has read the settings: until then HasFolders is unknown, not false.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowConnectCard), nameof(HasNotice))]
    private bool _isLoaded;

    [ObservableProperty] private bool _hasShelf;
    [ObservableProperty] private bool _hasPinned;
    [ObservableProperty] private bool _hasRecentlyAdded;
    [ObservableProperty] private bool _hasOnRepeat;

    /// <summary>The Shelf as a horizontal rail of covers (true) or a vertical list (false).</summary>
    [ObservableProperty] private bool _isShelfGrid = true;

    /// <summary>First launch: no folder yet, so the only thing to show is the way in. Held
    /// back until the settings have loaded, or a user with folders sees it flash on launch.</summary>
    public bool ShowConnectCard => IsLoaded && !HasFolders;

    /// <summary>Any card above the Library sections (connect, reconnect, scan progress), so an
    /// empty card area takes no room.</summary>
    public bool HasNotice => ShowConnectCard || NeedsReconnect || IsScanning;

    /// <summary>The plain status line; the reconnect card carries the text while it is up.</summary>
    public bool ShowStatusLine => !NeedsReconnect && !string.IsNullOrEmpty(StatusText);

    /// <summary>Every track, sorted by title. Rebuilt with one Reset on each library update.</summary>
    public BulkObservableCollection<Track> Songs { get; } = new();

    /// <summary>The configured roots (SAF tree URIs on Android), mirrored from settings.</summary>
    public ObservableCollection<string> Folders { get; } = new();

    public IReadOnlyList<Playlist> Playlists => _playlists;

    /// <summary>Recently played albums, newest first (the persisted play log).</summary>
    public BulkObservableCollection<Album> Shelf { get; } = new();

    public BulkObservableCollection<RailItem> PinnedRail { get; } = new();
    public BulkObservableCollection<RailItem> RecentlyAddedRail { get; } = new();
    public BulkObservableCollection<RailItem> OnRepeatRail { get; } = new();

    /// <summary>The Shelf's albums as rail tiles: Library → Recently Played.</summary>
    public BulkObservableCollection<RailItem> RecentlyPlayedRail { get; } = new();

    public IEnumerable<Track> Favourites() => Songs.Where(t => t.IsFavorite);

    public IEnumerable<Track> RecentlyAdded()
    {
        var cutoff = DateTime.UtcNow.AddDays(-RecentlyAddedDays);
        return _library.Tracks.Where(t => t.DateAdded >= cutoff).OrderByDescending(t => t.DateAdded);
    }

    public bool IsAlbumPinned(Guid albumId) => _pinnedAlbumIds.Contains(albumId);

    public bool IsArtistPinned(string name) => _pinnedArtistNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    public bool IsTrackPinned(Guid trackId) => _pinnedTrackIds.Contains(trackId);

    /// <summary>The On Repeat rail's songs, in rail order (its › list).</summary>
    public IEnumerable<Track> OnRepeatTracks() => OnRepeatRail.Select(r => r.Payload).OfType<Track>();

    /// <summary>A playlist's cover: the first of its tracks that has one.</summary>
    public string? PlaylistArtwork(Playlist playlist) =>
        playlist.TrackIds.Select(_library.GetTrackById).FirstOrDefault(t => !string.IsNullOrEmpty(t?.AlbumArtworkPath))?.AlbumArtworkPath;

    /// <summary>Pin or unpin an album on the Pinned rail (AppSettings.PinnedAlbumIds).</summary>
    public async Task SetAlbumPinnedAsync(Guid albumId, bool pinned)
    {
        var settings = await _persistence.LoadSettingsAsync();
        settings.PinnedAlbumIds.RemoveAll(id => id == albumId);
        if (pinned) settings.PinnedAlbumIds.Add(albumId);
        await SaveSettingsAsync(settings, "Pin");
        _pinnedAlbumIds = settings.PinnedAlbumIds.ToList();
        AfterUserEdit();
    }

    /// <summary>Pin or unpin an artist on the Pinned rail (AppSettings.PinnedArtistNames).</summary>
    public async Task SetArtistPinnedAsync(string name, bool pinned)
    {
        var settings = await _persistence.LoadSettingsAsync();
        settings.PinnedArtistNames.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        if (pinned) settings.PinnedArtistNames.Add(name);
        await SaveSettingsAsync(settings, "Pin");
        _pinnedArtistNames = settings.PinnedArtistNames.ToList();
        AfterUserEdit();
    }

    /// <summary>Pin or unpin a song on the Pinned rail (AppSettings.PinnedTrackIds).</summary>
    public async Task SetTrackPinnedAsync(Guid trackId, bool pinned)
    {
        var settings = await _persistence.LoadSettingsAsync();
        settings.PinnedTrackIds.RemoveAll(id => id == trackId);
        if (pinned) settings.PinnedTrackIds.Add(trackId);
        await SaveSettingsAsync(settings, "Pin");
        _pinnedTrackIds = settings.PinnedTrackIds.ToList();
        AfterUserEdit();
    }

    public Task SetPlaylistPinnedAsync(Playlist playlist, bool pinned)
    {
        playlist.IsPinned = pinned;
        return EditPlaylistAsync(playlist, p => p.IsPinned = pinned);
    }

    public async Task<Playlist> CreatePlaylistAsync(string name)
    {
        var playlist = new Playlist { Name = name };
        await EditPlaylistsAsync(list => list.Add(playlist));
        return playlist;
    }

    /// <summary>Appends <paramref name="tracks"/> in order, skipping any already in the playlist
    /// (desktop parity with SidebarViewModel.AddTracksToPlaylist: both apps share playlists.json).</summary>
    public Task AddToPlaylistAsync(Playlist playlist, IReadOnlyList<Track> tracks)
    {
        Append(playlist, tracks);
        return EditPlaylistAsync(playlist, p => Append(p, tracks));
    }

    private static void Append(Playlist playlist, IReadOnlyList<Track> tracks)
    {
        var existing = new HashSet<Guid>(playlist.TrackIds);
        foreach (var t in tracks)
        {
            if (existing.Add(t.Id)) playlist.TrackIds.Add(t.Id);
        }
        playlist.ModifiedAt = DateTime.UtcNow;
    }

    /// <summary>Applies an edit of one playlist to the copy in playlists.json now, found by id
    /// (the caller's instance may be from before a reload). Gone from the file (the desktop
    /// deleted it, sign-out removed it): nothing to edit.</summary>
    private Task EditPlaylistAsync(Playlist playlist, Action<Playlist> edit) => EditPlaylistsAsync(list =>
    {
        if (list.FirstOrDefault(p => p.Id == playlist.Id) is { } current) edit(current);
    });

    /// <summary>
    /// Re-read, edit, save: the account sync also writes playlists.json (the desktop's playlists),
    /// so saving the list this view model read earlier would drop what the sync wrote — or push
    /// the old copy of a desktop playlist back to the desktop as a newer edit.
    /// </summary>
    private async Task EditPlaylistsAsync(Action<List<Playlist>> edit)
    {
        try
        {
            _playlists = await _persistence.LoadPlaylistsAsync();
        }
        catch (Exception ex)
        {
            // Edit what this view model has, as before; better than losing the edit.
            DebugLog.Write("Library", $"Playlist re-read failed: {ex.Message}");
        }
        edit(_playlists);
        await SavePlaylistsAsync();
    }

    private async Task SavePlaylistsAsync()
    {
        try
        {
            await _persistence.SavePlaylistsAsync(_playlists);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Library", $"Playlist save failed: {ex.Message}");
        }
        PlaylistCount = _playlists.Count;
        AfterUserEdit();
    }

    /// <summary>
    /// A settings write from a user action. Logged, never thrown: these run from async command
    /// and sheet handlers, and an exception escaping one reaches the UI thread and ends the app.
    /// The edit still applies for this session, as a failed playlist save does.
    /// </summary>
    private async Task SaveSettingsAsync(AppSettings settings, string what)
    {
        try
        {
            await _persistence.SaveSettingsAsync(settings);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Library", $"{what} settings save failed: {ex.Message}");
        }
    }

    /// <summary>A pin or playlist edit: rails and open pages re-read, the library itself is unchanged.</summary>
    private void AfterUserEdit()
    {
        RefreshRails();
        Refreshed?.Invoke(this, EventArgs.Empty);
    }

    public async Task InitializeAsync()
    {
        try
        {
            var settings = await _persistence.LoadSettingsAsync();
            SetFolders(settings.MusicFolders);
            _pinnedAlbumIds = settings.PinnedAlbumIds.ToList();
            _pinnedArtistNames = settings.PinnedArtistNames.ToList();
            _pinnedTrackIds = settings.PinnedTrackIds.ToList();
            await LoadPlaylistsAsync();
            RefreshFromLibrary();
        }
        finally
        {
            // Even after a failed load: otherwise an empty library would show no connect card
            // and so no way to add music.
            IsLoaded = true;
        }
    }

    [RelayCommand] private void ToggleShelfLayout() => IsShelfGrid = !IsShelfGrid;

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        var picked = await _picker.PickFolderAsync();
        if (string.IsNullOrWhiteSpace(picked)) return;

        var settings = await _persistence.LoadSettingsAsync();
        if (!settings.MusicFolders.Contains(picked))
        {
            settings.MusicFolders.Add(picked);
            await SaveSettingsAsync(settings, "Folder");
        }
        SetFolders(settings.MusicFolders);
        await ScanAsync(settings.MusicFolders);
    }

    [RelayCommand]
    private async Task RescanAsync()
    {
        var settings = await _persistence.LoadSettingsAsync();
        if (settings.MusicFolders.Count == 0) return;
        await ScanAsync(settings.MusicFolders);
    }

    private async Task ScanAsync(IEnumerable<string> folders)
    {
        if (IsScanning) return;
        IsScanning = true;
        NeedsReconnect = false;
        ScanProgress = 0;
        StatusText = "Scanning…";
        try
        {
            await _library.ScanAsync(folders);
            if (StatusText == "Scanning…") StatusText = string.Empty; // ScanAborted may have replaced it
        }
        catch (Exception ex)
        {
            DebugLog.Write("Library", $"Phone scan failed: {ex.Message}");
            StatusText = "Scan failed. See the log.";
        }
        finally
        {
            IsScanning = false;
            RefreshFromLibrary();
        }
    }

    /// <summary>Re-reads playlists.json after something other than this view model wrote it
    /// (the desktop account sync), then refreshes the tabs that list playlists.</summary>
    public async Task ReloadPlaylistsAsync()
    {
        await LoadPlaylistsAsync();
        RefreshFromLibrary();
    }

    private async Task LoadPlaylistsAsync()
    {
        try
        {
            _playlists = await _persistence.LoadPlaylistsAsync();
        }
        catch (Exception ex)
        {
            DebugLog.Write("Library", $"Playlist load failed: {ex.Message}");
            _playlists = new List<Playlist>();
        }
        PlaylistCount = _playlists.Count;
    }

    private void SetFolders(IEnumerable<string> folders)
    {
        Folders.Clear();
        foreach (var f in folders) Folders.Add(f);
        HasFolders = Folders.Count > 0;
    }

    private void RefreshFromLibrary()
    {
        var tracks = _library.Tracks;
        SongCount = tracks.Count;
        AlbumCount = _library.Albums.Count;
        ArtistCount = _library.Artists.Count;
        FavoriteCount = tracks.Count(t => t.IsFavorite);
        var cutoff = DateTime.UtcNow.AddDays(-RecentlyAddedDays);
        RecentlyAddedCount = tracks.Count(t => t.DateAdded >= cutoff);
        Songs.ReplaceAll(tracks.OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase));
        RefreshRails();
        RefreshRecents();
        Refreshed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Pinned and Recently Added: library structure and pins only.</summary>
    private void RefreshRails()
    {
        // Albums, artists, playlists, then songs; pins whose item left the library are skipped.
        var artistArt = _pinnedArtistNames.Count > 0 ? MobileLibrary.ArtistArtwork(_library) : null;
        var pinnedArtists = _pinnedArtistNames
            .Select(n => _library.Artists.FirstOrDefault(a => string.Equals(a.Name, n, StringComparison.OrdinalIgnoreCase)))
            .OfType<Artist>()
            .Select(a => RailItem.ForArtist(a, artistArt?.GetValueOrDefault(a.Name)));
        var pinned = _pinnedAlbumIds.Select(_library.GetAlbumById).OfType<Album>().Select(RailItem.ForAlbum)
            .Concat(pinnedArtists)
            .Concat(_playlists.Where(p => p.IsPinned).Select(p => RailItem.ForPlaylist(p, PlaylistArtwork(p))))
            .Concat(_pinnedTrackIds.Select(_library.GetTrackById).OfType<Track>().Select(RailItem.ForTrack))
            .ToList();
        MobileLibrary.ReplaceIfChanged(PinnedRail, pinned);
        HasPinned = PinnedRail.Count > 0;

        var recent = MobileLibrary.RecentlyAddedAlbums(_library, RailSize).Select(RailItem.ForAlbum).ToList();
        MobileLibrary.ReplaceIfChanged(RecentlyAddedRail, recent);
        HasRecentlyAdded = RecentlyAddedRail.Count > 0;
    }

    /// <summary>
    /// Shelf and On Repeat: the play log. Called on every library refresh and by the shell
    /// whenever a track starts (the play is recorded before CurrentTrack changes).
    /// </summary>
    public void RefreshRecents()
    {
        var events = _history?.Events ?? Array.Empty<PlayHistoryEvent>();

        var recent = HomeRowsBuilder.BuildRecentFromLog(events, _library.GetTrackById, MobileLibrary.RecentLogScan);
        var shelf = MobileLibrary.RecentAlbums(_library, recent, ShelfSize);
        MobileLibrary.ReplaceIfChanged(Shelf, shelf);
        HasShelf = Shelf.Count > 0;
        MobileLibrary.ReplaceIfChanged(RecentlyPlayedRail, shelf.Select(RailItem.ForAlbum).ToList());

        var onRepeat = HomeRowsBuilder.BuildHeavyRotation(events, DateTime.Now, top: RailSize)
            .Select(_library.GetTrackById).OfType<Track>()
            .Select(RailItem.ForTrack).ToList();
        MobileLibrary.ReplaceIfChanged(OnRepeatRail, onRepeat);
        HasOnRepeat = OnRepeatRail.Count > 0;
    }
}
