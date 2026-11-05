using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Mobile.Services;
using Noctis.Models;
using Noctis.Services;
using Noctis.Localization;

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
        _library.FavoritesChanged += (_, _) => _marshal(RefreshFavourites);
        _library.ScanProgress += (_, n) => _marshal(() => ScanProgress = n);
        _library.ScanAborted += (_, all) => _marshal(() =>
        {
            // The phone's music index out of reach is the permission taken away, not a folder:
            // it has its own card (AllMusicLost), set when the scan started.
            var roots = all.Where(r => !DeviceMusicScan.IsDeviceMusicPath(r)).ToArray();
            if (roots.Length == 0) return;
            UnavailableFolders = roots;
            NeedsReconnect = true;
            StatusText = roots.Length == 1
                ? Loc.T("Mobile.Library.FolderUnavailable", FolderName(roots[0]))
                : Loc.Plural("Mobile.Library.FoldersUnavailable", roots.Length);
        });
    }

    /// <summary>The shared Core library, for the pages built on this view model.</summary>
    public ILibraryService Service => _library;

    /// <summary>Artist photos for the pinned artists' circles (the shell hands its source over);
    /// null leaves their covers.</summary>
    public IArtistPhotoSource? ArtistPhotos { get; set; }

    /// <summary>Pinned artists whose photo was asked for this session: a lookup that found nothing
    /// (or failed) is not repeated on every refresh.</summary>
    private readonly HashSet<string> _pinnedPhotoAsked = new(StringComparer.OrdinalIgnoreCase);

    public IPersistenceService Persistence => _persistence;

    /// <summary>The persisted play log (null in hosts without one).</summary>
    public IPlayHistoryService? History => _history;

    /// <summary>Raised on the UI thread after every rebuild (library update, favourites, playlists, pins).</summary>
    public event EventHandler? Refreshed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMusic), nameof(ShowConnectCard), nameof(HasNotice), nameof(ShowSections))]
    private int _songCount;
    [ObservableProperty] private int _albumCount;
    [ObservableProperty] private int _artistCount;
    [ObservableProperty] private int _favoriteCount;
    [ObservableProperty] private int _recentlyAddedCount;
    [ObservableProperty] private int _playlistCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private bool _isScanning;
    [ObservableProperty] private int _scanProgress;

    /// <summary>When this run's last scan finished (local time); null until one has. Settings → Rescan Library says so.</summary>
    [ObservableProperty] private DateTime? _lastScanLocal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMusic), nameof(ShowConnectCard), nameof(HasNotice), nameof(ShowSections))]
    private bool _hasFolders;

    /// <summary>A folder, or songs without one (a computer's, signed in with no folder on the
    /// phone): the Library rows, Edit and the sections show. Gated on a folder alone, a phone
    /// streaming its computer's library saw only "Add your music".</summary>
    public bool HasMusic => HasFolders || AllMusic || SongCount > 0;

    /// <summary>"All Music on This Phone" (null in hosts without Android's media index: the
    /// option is hidden there). The host sets it before the library loads.</summary>
    public IDeviceMusic? DeviceMusic { get; set; }

    public bool CanFindAllMusic => DeviceMusic != null;

    /// <summary>Settings › Music Folders › Find All Music Automatically (AppSettings.PhoneAllMusic):
    /// every song in the phone's media index joins the folders' songs. Turning it on asks for the
    /// permission first; refused, it stays off and <see cref="AllMusicDenied"/> explains.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMusic), nameof(ShowConnectCard), nameof(HasNotice), nameof(ShowSections), nameof(ShowAllMusicLost),
        nameof(ShowAllMusicHelp), nameof(AllMusicHelpText))]
    private bool _allMusic;

    /// <summary>The permission was just refused: the switch and the first-run card say how to
    /// allow it in the system settings.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAllMusicHelp), nameof(AllMusicHelpText))]
    private bool _allMusicDenied;

    /// <summary>The option is on but the permission was taken away since: the songs found stay
    /// listed (with their favourites and plays) but cannot play until it is allowed again.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAllMusicLost), nameof(HasNotice), nameof(ShowAllMusicHelp), nameof(AllMusicHelpText))]
    private bool _allMusicLost;

    public bool ShowAllMusicLost => AllMusic && AllMusicLost;

    /// <summary>Under the switch: why the permission is needed and the way to the system settings.</summary>
    public bool ShowAllMusicHelp => AllMusicDenied || ShowAllMusicLost;

    public string AllMusicHelpText => ShowAllMusicLost ? Loc.T("Mobile.Folders.AllMusicLost") : Loc.T("Mobile.Folders.AllMusicDenied");

    /// <summary>The first-run card's text: with Find All Music offered, it names both ways in.</summary>
    public string ConnectCardText => CanFindAllMusic ? Loc.T("Mobile.Notice.AddMusicTextAll") : Loc.T("Mobile.Notice.AddMusicText");

    private bool _applyingAllMusic;

    /// <summary>A refusal while turning the option on: allowed later in the system settings, it
    /// turns on when the app comes back (<see cref="RecheckDeviceMusic"/>).</summary>
    private bool _allMusicWanted;

    partial void OnAllMusicChanged(bool value)
    {
        if (_applyingAllMusic) return;
        _ = SetAllMusicAsync(value);
    }

    private void ApplyAllMusic(bool value)
    {
        _applyingAllMusic = true;
        AllMusic = value;
        _applyingAllMusic = false;
    }

    /// <summary>The switch (or the first-run card) turned the option on or off.</summary>
    internal async Task SetAllMusicAsync(bool on)
    {
        if (on)
        {
            var device = DeviceMusic;
            var granted = false;
            if (device != null)
            {
                try
                {
                    granted = device.HasPermission || await device.RequestPermissionAsync();
                }
                catch (Exception ex)
                {
                    DebugLog.Write("Library", $"Music permission request failed: {ex.Message}");
                }
            }
            if (!granted)
            {
                _allMusicWanted = device != null;
                _marshal(() =>
                {
                    ApplyAllMusic(false);
                    AllMusicDenied = device != null;
                });
                return;
            }
            _allMusicWanted = false;
            AllMusicDenied = false;
            AllMusicLost = false;
        }
        else
        {
            _allMusicWanted = false;
            AllMusicDenied = false;
            AllMusicLost = false;
        }
        _marshal(() => ApplyAllMusic(on));
        await UpdateSettingsAsync(s => s.PhoneAllMusic = on, nameof(AppSettings.PhoneAllMusic));
        // Off: a pass must run even with no folder left, to take the songs out.
        await ScanAsync(evenWithoutFolders: !on);
    }

    /// <summary>First-run card: Find All Music.</summary>
    [RelayCommand] private void FindAllMusic() => AllMusic = true;

    /// <summary>Noctis's page in the system settings, where a refused permission is allowed.</summary>
    [RelayCommand] private void OpenAppSettings() => DeviceMusic?.OpenAppSettings();

    /// <summary>The app came back to the front (maybe from the system settings): a permission
    /// allowed there turns the option on (or back on) without another tap; one taken away
    /// there shows.</summary>
    public void RecheckDeviceMusic()
    {
        if (DeviceMusic is not { } device) return;
        var has = device.HasPermission;
        if (has && _allMusicWanted && !AllMusic)
        {
            AllMusic = true;
            return;
        }
        if (has && AllMusicLost)
        {
            AllMusicLost = false;
            _ = ScanAsync();
        }
        else if (!has && AllMusic)
        {
            AllMusicLost = true;
        }
    }

    /// <summary>The last scan aborted on an unreadable root (a revoked SAF grant, spec §7):
    /// the page offers to re-pick the folder, which is how a dead grant is recovered.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStatusLine), nameof(HasNotice))]
    private bool _needsReconnect;

    /// <summary>The folders the last scan could not reach (a card taken out, a folder deleted),
    /// set with <see cref="NeedsReconnect"/>; Settings marks them.</summary>
    public IReadOnlyList<string> UnavailableFolders { get; private set; } = Array.Empty<string>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStatusLine))]
    private string _statusText = string.Empty;

    /// <summary>InitializeAsync has read the settings: until then HasFolders is unknown, not false.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowConnectCard), nameof(HasNotice), nameof(ShowSections))]
    private bool _isLoaded;

    [ObservableProperty] private bool _hasShelf;
    [ObservableProperty] private bool _hasPinned;
    [ObservableProperty] private bool _hasRecentlyAdded;
    [ObservableProperty] private bool _hasOnRepeat;

    /// <summary>The Shelf as a horizontal rail of covers (true) or a vertical list (false).</summary>
    [ObservableProperty] private bool _isShelfGrid = true;

    /// <summary>The Library tab's list rows as the user arranged them (AppSettings.PhoneLibraryRows,
    /// read at start; see LibraryRowsViewModel for the format). Empty = the default set.</summary>
    [ObservableProperty] private IReadOnlyList<string> _libraryRowKeys = Array.Empty<string>();

    /// <summary>First launch: no folder yet, so the only thing to show is the way in. Held
    /// back until the settings have loaded, or a user with folders sees it flash on launch.</summary>
    public bool ShowConnectCard => IsLoaded && !HasMusic;

    /// <summary>The sections under the rows (Pinned and its hint): the rows show from the settings
    /// alone (<see cref="ApplySettings"/>), the sections wait for the library, or the pin hint
    /// flashed over a Pinned rail still loading.</summary>
    public bool ShowSections => IsLoaded && HasMusic;

    /// <summary>Any card above the Library sections (connect, reconnect, scan progress), so an
    /// empty card area takes no room.</summary>
    public bool HasNotice => ShowConnectCard || NeedsReconnect || IsScanning || ShowAllMusicLost;

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

    /// <summary>
    /// Recently Added's › list: the rail's rule (<see cref="MobileLibrary.RecentlyAddedAlbums"/>,
    /// newest album first, no time limit) with each album's songs in track order. It took only
    /// the last 30 days, newest file first: a library with nothing new showed a full rail over an
    /// empty page, and a fresh album listed its songs backwards (the last file scanned is newest).
    /// </summary>
    public IEnumerable<Track> RecentlyAdded()
    {
        var tracks = _library.Tracks;
        var albumNewest = tracks.Where(t => t.AlbumId != Guid.Empty).GroupBy(t => t.AlbumId)
            .ToDictionary(g => g.Key, g => g.Max(t => t.DateAdded));
        DateTime Added(Track t) => t.AlbumId != Guid.Empty ? albumNewest[t.AlbumId] : t.DateAdded;
        return tracks.OrderByDescending(Added).ThenBy(t => t.AlbumId)
            .ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase);
    }

    public bool IsAlbumPinned(Guid albumId) => _pinnedAlbumIds.Contains(albumId);

    public bool IsArtistPinned(string name) => _pinnedArtistNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    public bool IsTrackPinned(Guid trackId) => _pinnedTrackIds.Contains(trackId);

    /// <summary>The On Repeat rail's songs, in rail order (its › list), as the library holds them
    /// now: the rail keeps an unchanged tile across refreshes, and with it a Track from before a
    /// rescan that re-read the file.</summary>
    public IEnumerable<Track> OnRepeatTracks() =>
        OnRepeatRail.Select(r => r.Payload).OfType<Track>().Select(t => _library.GetTrackById(t.Id) ?? t);

    /// <summary>A playlist's cover: the first of its tracks that has one.</summary>
    public string? PlaylistArtwork(Playlist playlist) =>
        playlist.TrackIds.Select(_library.GetTrackById).FirstOrDefault(t => !string.IsNullOrEmpty(t?.AlbumArtworkPath))?.AlbumArtworkPath;

    /// <summary>Pin or unpin an album on the Pinned rail (AppSettings.PinnedAlbumIds).</summary>
    public async Task SetAlbumPinnedAsync(Guid albumId, bool pinned)
    {
        void Pin(List<Guid> ids)
        {
            ids.RemoveAll(id => id == albumId);
            if (pinned) ids.Add(albumId);
        }
        List<Guid>? saved = null;
        await UpdateSettingsAsync(s => { Pin(s.PinnedAlbumIds); saved = s.PinnedAlbumIds.ToList(); }, "Pin");
        if (saved == null) Pin(saved = _pinnedAlbumIds.ToList());   // a failed save still applies this session
        _pinnedAlbumIds = saved;
        AfterUserEdit();
    }

    /// <summary>Pin or unpin an artist on the Pinned rail (AppSettings.PinnedArtistNames).</summary>
    public async Task SetArtistPinnedAsync(string name, bool pinned)
    {
        void Pin(List<string> names)
        {
            names.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            if (pinned) names.Add(name);
        }
        List<string>? saved = null;
        await UpdateSettingsAsync(s => { Pin(s.PinnedArtistNames); saved = s.PinnedArtistNames.ToList(); }, "Pin");
        if (saved == null) Pin(saved = _pinnedArtistNames.ToList());
        _pinnedArtistNames = saved;
        AfterUserEdit();
    }

    /// <summary>Pin or unpin a song on the Pinned rail (AppSettings.PinnedTrackIds).</summary>
    public async Task SetTrackPinnedAsync(Guid trackId, bool pinned)
    {
        void Pin(List<Guid> ids)
        {
            ids.RemoveAll(id => id == trackId);
            if (pinned) ids.Add(trackId);
        }
        List<Guid>? saved = null;
        await UpdateSettingsAsync(s => { Pin(s.PinnedTrackIds); saved = s.PinnedTrackIds.ToList(); }, "Pin");
        if (saved == null) Pin(saved = _pinnedTrackIds.ToList());
        _pinnedTrackIds = saved;
        AfterUserEdit();
    }

    /// <summary>Saves the Library tab's row order and on/off state (Edit → Done).</summary>
    public async Task SaveLibraryRowKeysAsync(IReadOnlyList<string> keys)
    {
        var rows = keys.ToList();
        LibraryRowKeys = rows;
        await UpdateSettingsAsync(s => s.PhoneLibraryRows = rows.ToList(), "Library rows");
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
        var now = DateTime.UtcNow;
        Append(playlist, tracks, now);
        return EditPlaylistAsync(playlist, p => Append(p, tracks, now));
    }

    /// <summary>Desktop parity (SidebarViewModel.RenamePlaylist): the new name and a ModifiedAt
    /// bump, which is what carries the rename to the computer on the next sync.</summary>
    public Task RenamePlaylistAsync(Playlist playlist, string name)
    {
        var now = DateTime.UtcNow;
        return EditPlaylistAsync(playlist, p =>
        {
            p.Name = name;
            p.ModifiedAt = now;
        });
    }

    /// <summary>Desktop parity (SidebarViewModel.DeletePlaylistAsync): the playlist leaves
    /// playlists.json, and the Pinned rail with it.</summary>
    public Task DeletePlaylistAsync(Playlist playlist) =>
        EditPlaylistsAsync(list => list.RemoveAll(p => p.Id == playlist.Id));

    /// <summary>Desktop parity (PlaylistViewModel.RemoveTrack): the songs leave the playlist with
    /// their "added" stamps (TrackAddedAt), and ModifiedAt moves so the sync carries it. Not for
    /// a smart playlist, whose songs come from its rules.</summary>
    public Task RemoveFromPlaylistAsync(Playlist playlist, IReadOnlyList<Track> tracks)
    {
        if (playlist.IsSmartPlaylist || tracks.Count == 0) return Task.CompletedTask;
        var now = DateTime.UtcNow;
        var ids = tracks.Select(t => t.Id).ToHashSet();
        return EditPlaylistAsync(playlist, p =>
        {
            if (p.TrackIds.RemoveAll(ids.Contains) == 0) return;
            foreach (var id in ids) p.TrackAddedAt.Remove(id);
            p.ModifiedAt = now;
        });
    }

    /// <summary>Desktop parity (SidebarViewModel.AddTracksToPlaylist): each added song is stamped
    /// with when it joined, which the playlist page's Date Added sort reads.</summary>
    private static void Append(Playlist playlist, IReadOnlyList<Track> tracks, DateTime now)
    {
        var existing = new HashSet<Guid>(playlist.TrackIds);
        foreach (var t in tracks)
        {
            if (!existing.Add(t.Id)) continue;
            playlist.TrackIds.Add(t.Id);
            playlist.TrackAddedAt[t.Id] = now;
        }
        playlist.ModifiedAt = now;
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
    /// A settings write from a user action: read, change and save with no other save of the file
    /// in between (a plain load-then-save let another writer's save land in the gap, and the stale
    /// copy written after it undid that save). <paramref name="change"/> runs off the UI thread
    /// and only edits the settings. Logged, never thrown: these run from async command and sheet
    /// handlers, and an exception escaping one reaches the UI thread and ends the app. The edit
    /// still applies for this session, as a failed playlist save does.
    /// </summary>
    private async Task UpdateSettingsAsync(Action<AppSettings> change, string what)
    {
        try
        {
            await _persistence.UpdateSettingsAsync(change);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Library", $"{what} settings save failed: {ex.Message}");
        }
    }

    /// <summary>The desktop songs' offline copies changed: open lists (Downloaded, the Downloaded
    /// filter) re-read.</summary>
    public void NotifyDownloadsChanged() => Refreshed?.Invoke(this, EventArgs.Empty);

    /// <summary>The UI language changed: the rails' subtitles ("Artist", "12 songs") and the open
    /// pages' rows are rebuilt; the library itself is unchanged.</summary>
    public void Relabel() => AfterUserEdit();

    /// <summary>A pin or playlist edit: rails and open pages re-read, the library itself is unchanged.</summary>
    private void AfterUserEdit()
    {
        RefreshRails();
        Refreshed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// What the settings alone decide: the folders (so the Library rows show) and the rows'
    /// order, and the pins. The host applies these as soon as it has read the settings at start,
    /// before the play log and the library load: the Library tab sat as a bare title for that
    /// whole load on a cold launch (~1 s on the emulator, longer with a big library).
    /// </summary>
    public void ApplySettings(AppSettings settings)
    {
        SetFolders(settings.MusicFolders);
        ApplyAllMusic(settings.PhoneAllMusic && DeviceMusic != null);
        // Not over the user's own flip: the start re-reads the settings (InitializeAsync) after the
        // host applied them, and a flip in between was put back (its save still in flight).
        if (!_scanOnStartupSetByUser)
        {
            _applyingSettings = true;
            ScanOnStartup = settings.ScanOnStartup;
            _applyingSettings = false;
        }
        _pinnedAlbumIds = settings.PinnedAlbumIds.ToList();
        _pinnedArtistNames = settings.PinnedArtistNames.ToList();
        _pinnedTrackIds = settings.PinnedTrackIds.ToList();
        LibraryRowKeys = settings.PhoneLibraryRows.ToList();
    }

    public async Task InitializeAsync()
    {
        try
        {
            ApplySettings(await _persistence.LoadSettingsAsync());
            await LoadPlaylistsAsync();
            RefreshFromLibrary();
        }
        finally
        {
            // Even after a failed load: otherwise an empty library would show no connect card
            // and so no way to add music.
            IsLoaded = true;
            if (_scanWhenLoaded)
            {
                _scanWhenLoaded = false;
                _ = ScanAsync();
            }
        }
    }

    /// <summary>A scan asked for before the library loaded (<see cref="ScanAsync"/>), run once it has.</summary>
    private bool _scanWhenLoaded;

    [RelayCommand] private void ToggleShelfLayout() => IsShelfGrid = !IsShelfGrid;

    /// <summary>Whether <paramref name="folder"/> is still readable; see IFolderPicker.HasAccess.</summary>
    public bool HasAccess(string folder) => _picker.HasAccess(folder);

    /// <summary>The storage <paramref name="folder"/> is on when not the phone's own; see IFolderPicker.StorageName.</summary>
    public string? StorageName(string folder) => _picker.StorageName(folder);

    /// <summary>
    /// Desktop parity (SettingsViewModel.AddFolderPath): the same folder again is silent (and
    /// rescans, which is how a folder whose access was lost comes back); a folder inside one
    /// already listed, or around one, is refused with a note, as the scan would list its songs
    /// twice (a SAF song's path carries the folder it was found through).
    /// Concurrent: it awaits the scan it starts (minutes over SAF), and a command that may not
    /// overlap is greyed out while it runs; a folder added mid-scan gets the second pass.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task AddFolderAsync()
    {
        var picked = await _picker.PickFolderAsync();
        if (string.IsNullOrWhiteSpace(picked)) return;

        string? refusal = null;
        List<string>? folders = null;
        await UpdateSettingsAsync(settings =>
        {
            if (!settings.MusicFolders.Any(f => FolderKey(f) == FolderKey(picked)))
            {
                if (settings.MusicFolders.FirstOrDefault(f => IsInside(picked, f)) is { } covering)
                    refusal = Loc.T("Mobile.Library.AlreadyCovered", FolderName(covering));
                else if (settings.MusicFolders.FirstOrDefault(f => IsInside(f, picked)) is { } inner)
                    refusal = Loc.T("Mobile.Library.InsideFolder", FolderName(inner));
                else
                    settings.MusicFolders.Add(picked);
            }
            folders = settings.MusicFolders.ToList();
        }, "Folder");
        if (refusal != null)
        {
            RefusePick(picked, refusal);
            return;
        }
        SetFolders(folders ?? Folders.Append(picked).Distinct().ToList());
        await ScanAsync();
    }

    /// <summary>
    /// Desktop parity (SettingsViewModel.RemoveFolder): the folder leaves the list and a rescan
    /// takes its songs out of the library (removing the last one empties it). The files stay
    /// where they are; the phone gives up its access to the folder.
    /// </summary>
    public async Task RemoveFolderAsync(string folder)
    {
        var removed = 0;
        List<string>? folders = null;
        await UpdateSettingsAsync(settings =>
        {
            removed = settings.MusicFolders.RemoveAll(f => f == folder);
            folders = settings.MusicFolders.ToList();
        }, "Folder");
        if (folders != null && removed == 0) return;
        SetFolders(folders ?? Folders.Where(f => f != folder).ToList());
        _picker.ReleaseAccess(folder);
        await ScanAsync(evenWithoutFolders: true);
    }

    /// <summary>A refused pick: say why (cleared after a while, as on the desktop) and give the
    /// access just granted back, as nothing will use it.</summary>
    private void RefusePick(string picked, string why)
    {
        _picker.ReleaseAccess(picked);
        StatusText = why;
        _ = ClearStatusLaterAsync(why);
    }

    private async Task ClearStatusLaterAsync(string text)
    {
        await Task.Delay(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
        _marshal(() =>
        {
            if (StatusText == text) StatusText = string.Empty;
        });
    }

    /// <summary>What identifies a folder: a SAF tree URI's provider and its document id
    /// ("content://…/tree/primary%3AMusic%2FTones" → "content://…", "primary:Music/Tones");
    /// anything else as a plain path.</summary>
    internal static (string Scope, string Path) FolderKey(string folder)
    {
        var tree = folder.IndexOf("/tree/", StringComparison.Ordinal);
        if (tree < 0) return (string.Empty, folder.Replace('\\', '/').TrimEnd('/'));
        return (folder[..tree], Uri.UnescapeDataString(folder[(tree + "/tree/".Length)..]).TrimEnd('/'));
    }

    /// <summary>True when <paramref name="folder"/> is below <paramref name="root"/> (not the
    /// same folder). "primary:Music/Tones" is under "primary:Music" and under a whole card
    /// ("1234-ABCD:"); "primary:MusicVideos" is not under "primary:Music".</summary>
    internal static bool IsInside(string folder, string root)
    {
        var (folderScope, folderPath) = FolderKey(folder);
        var (rootScope, rootPath) = FolderKey(root);
        if (folderScope != rootScope || folderPath.Length <= rootPath.Length
            || !folderPath.StartsWith(rootPath, StringComparison.Ordinal)) return false;
        return rootPath.EndsWith(':') || folderPath[rootPath.Length] == '/';
    }

    /// <summary>A folder's own name for a note ("Tones").</summary>
    private static string FolderName(string folder)
    {
        var path = FolderKey(folder).Path;
        var cut = Math.Max(path.LastIndexOf('/'), path.LastIndexOf(':'));
        return cut >= 0 && cut < path.Length - 1 ? path[(cut + 1)..] : path;
    }

    [RelayCommand]
    private Task RescanAsync() => ScanAsync();

    /// <summary>Desktop parity (Settings › Library › Scan on Startup, AppSettings.ScanOnStartup):
    /// each launch rescans the folders, so songs copied onto the phone since show up without a
    /// Rescan. Unchanged songs are not read again.</summary>
    [ObservableProperty] private bool _scanOnStartup = true;

    private bool _applyingSettings;
    private bool _scanOnStartupSetByUser;

    partial void OnScanOnStartupChanged(bool value)
    {
        if (_applyingSettings) return;
        _scanOnStartupSetByUser = true;
        _ = SaveScanOnStartupAsync(value);
    }

    private Task SaveScanOnStartupAsync(bool value) =>
        UpdateSettingsAsync(s => s.ScanOnStartup = value, "Scan on startup");

    /// <summary>The host calls this once the library has loaded at launch.</summary>
    public Task ScanAtStartupAsync() => ScanOnStartup && (HasFolders || AllMusic) ? ScanAsync() : Task.CompletedTask;

    /// <summary>Set when a scan is asked for while one runs: the running one goes again once it
    /// ends, so a folder added or removed mid-scan is never left out of (or in) the library.</summary>
    private bool _scanAgain;

    /// <summary>The last folder was removed: a pass with no folders must still run, to take its
    /// songs out. Any other scan with none does nothing (a settings read that came back empty
    /// must never empty the library).</summary>
    private bool _scanWithoutFolders;

    /// <summary>Scans the folders in the settings as they are when each pass starts.</summary>
    private async Task ScanAsync(bool evenWithoutFolders = false)
    {
        _scanWithoutFolders |= evenWithoutFolders;
        // At a cold start the folders (Rescan, Add Folder) show before the library has loaded.
        // A scan then started from an empty library while the load filled it: every song was
        // re-read as new (no favourites, plays, Date Added) and the two raced to publish.
        if (!IsLoaded)
        {
            _scanWhenLoaded = true;
            return;
        }
        if (IsScanning)
        {
            _scanAgain = true;
            return;
        }
        IsScanning = true;
        ScanProgress = 0;
        try
        {
            do
            {
                _scanAgain = false;
                // Each pass judges the folders afresh: one back in reach is no longer flagged.
                UnavailableFolders = Array.Empty<string>();
                NeedsReconnect = false;
                StatusText = Loc.T("Mobile.Library.Scanning");
                var settings = await _persistence.LoadSettingsAsync();
                var folders = settings.MusicFolders.ToList();
                if (settings.PhoneAllMusic && DeviceMusic is { } device)
                {
                    // Always walked while on: without the permission the root reads as out of
                    // reach, which keeps its songs as they were instead of dropping them.
                    device.Folders = folders.ToArray();
                    var lost = !device.HasPermission;
                    _marshal(() => AllMusicLost = lost);
                    folders.Add(DeviceMusicScan.Root);
                }
                if (folders.Count == 0 && !_scanWithoutFolders) break;
                _scanWithoutFolders = false;
                await _library.ScanAsync(folders);
            }
            while (_scanAgain);
            if (StatusText == Loc.T("Mobile.Library.Scanning")) StatusText = string.Empty; // ScanAborted may have replaced it
            if ((HasFolders || AllMusic) && !NeedsReconnect) LastScanLocal = DateTime.Now;
        }
        catch (Exception ex)
        {
            DebugLog.Write("Library", $"Phone scan failed: {ex.Message}");
            StatusText = Loc.T("Mobile.Library.ScanFailed");
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
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var tracks = _library.Tracks;
        SongCount = tracks.Count;
        AlbumCount = _library.Albums.Count;
        ArtistCount = _library.Artists.Count;
        FavoriteCount = tracks.Count(t => t.IsFavorite);
        var cutoff = DateTime.UtcNow.AddDays(-RecentlyAddedDays);
        RecentlyAddedCount = tracks.Count(t => t.DateAdded >= cutoff);
        Songs.ReplaceAll(tracks.OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase));
        var sorted = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        RefreshRails();
        RefreshRecents();
        Refreshed?.Invoke(this, EventArgs.Empty);
        // Runs on each library update, a scan's progressive publishes too (every 1.5 s while it
        // finds songs). Measured 10-20 on the emulator with 1206 songs: 5-12 ms each, 12-14 per
        // re-reading scan, 3 per scan with no changes. Logged when slow, so a big library or a
        // slow phone shows it.
        var total = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (total >= SlowRefreshMs)
            DebugLog.Write("Library", $"slow refresh: {tracks.Count} songs, sort {sorted:F0} ms, total {total:F0} ms (scanning={IsScanning})");
    }

    private const double SlowRefreshMs = 100;

    /// <summary>
    /// A heart toggled: the count and the open lists re-read (Favorites, a filtered list). The
    /// songs, albums and their order are what they were, so nothing is re-sorted or rebuilt:
    /// this used to run the full refresh, whose title sort of every song (culture-aware) held
    /// the UI thread for a good part of a second on a big library, on every tap of a heart.
    /// </summary>
    private void RefreshFavourites()
    {
        FavoriteCount = _library.Tracks.Count(t => t.IsFavorite);
        Refreshed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Pinned and Recently Added: library structure and pins only.</summary>
    private void RefreshRails()
    {
        // Albums, artists, playlists, then songs; pins whose item left the library are skipped.
        var artistArt = _pinnedArtistNames.Count > 0 ? MobileLibrary.ArtistArtwork(_library) : null;
        var pinnedArtistList = _pinnedArtistNames
            .Select(n => _library.Artists.FirstOrDefault(a => string.Equals(a.Name, n, StringComparison.OrdinalIgnoreCase)))
            .OfType<Artist>()
            .ToList();
        // The artist's photo when the phone has one, else the cover; a missing photo is asked for.
        var pinnedArtists = pinnedArtistList
            .Select(a => RailItem.ForArtist(a, ArtistPhotos?.CachedPhoto(a.Name) ?? artistArt?.GetValueOrDefault(a.Name)));
        foreach (var artist in pinnedArtistList) AskPinnedArtistPhoto(artist.Name);
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

    /// <summary>A pinned artist's circle shows on the Library tab: ask once a session for a photo
    /// the phone does not have yet, and rebuild the rails when one arrives.</summary>
    private void AskPinnedArtistPhoto(string name)
    {
        if (ArtistPhotos is not { } photos || photos.CachedPhoto(name) != null || !_pinnedPhotoAsked.Add(name)) return;
        _ = LoadPinnedArtistPhotoAsync(photos, name);
    }

    private async Task LoadPinnedArtistPhotoAsync(IArtistPhotoSource photos, string name)
    {
        try
        {
            if (await photos.GetPhotoAsync(name, CancellationToken.None) != null) _marshal(RefreshRails);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Artist", $"Pinned artist photo failed: {ex.Message}");
        }
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
