using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Helpers;
using Noctis.Services;
using Noctis.Views;

namespace Noctis.ViewModels;

/// <summary>
/// Manages sidebar navigation state and playlist list.
/// </summary>
public partial class SidebarViewModel : ViewModelBase
{
    private readonly IPersistenceService _persistence;
    private readonly ILibraryService _library;

    [ObservableProperty] private NavItem? _selectedNavItem;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private int _favoritesCount;

    /// <summary>Top-bar state mirrored by the rail's Back/Search actions.
    /// Set by MainWindowViewModel right after both ViewModels are constructed.</summary>
    [ObservableProperty] private TopBarViewModel? _topBar;

    /// <summary>Folders the user has collapsed this session (default expanded).</summary>
    private readonly HashSet<string> _collapsedFolders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Flattened sidebar playlist rows: pinned playlists first, then folder
    /// headers with their (indented) playlists, then loose playlists.
    /// </summary>
    public ObservableCollection<PlaylistNavItem> SidebarRows { get; } = new();

    /// <summary>Main navigation items (Home, Songs, Albums, Artists, Playlists, Settings).</summary>
    public ObservableCollection<NavItem> NavItems { get; } = new()
    {
        new NavItem { Key = "home", Label = "Home", IconGlyph = "HomeIcon" },
        new NavItem { Key = "songs", Label = "Songs", IconGlyph = "SongsIcon" },
        new NavItem { Key = "albums", Label = "Albums", IconGlyph = "AlbumsIcon" },
        new NavItem { Key = "artists", Label = "Artists", IconGlyph = "ArtistsIcon" },
        new NavItem { Key = "folders", Label = "Folders", IconGlyph = "FoldersIcon" },
        new NavItem { Key = "playlists", Label = "Playlists", IconGlyph = "PlaylistsIcon" },
        new NavItem { Key = "settings", Label = "Settings", IconGlyph = "SettingsIcon" },
    };

    /// <summary>Favorites navigation item (below divider).</summary>
    public ObservableCollection<NavItem> FavoritesItems { get; } = new()
    {
        new NavItem { Key = "favorites", Label = "Favorites", IconGlyph = "FavoritesIcon" },
    };

    /// <summary>User-created playlists shown in sidebar with artwork thumbnails.</summary>
    public ObservableCollection<PlaylistNavItem> PlaylistItems { get; } = new();

    /// <summary>The underlying playlist models as observable collection.</summary>
    public ObservableCollection<Playlist> Playlists { get; } = new();

    /// <summary>Fires when the user selects a different navigation item.</summary>
    public event EventHandler<string>? NavigationRequested;
    public event EventHandler<Guid>? PlaylistTracksChanged;

    public SidebarViewModel(IPersistenceService persistence, ILibraryService library)
    {
        _persistence = persistence;
        _library = library;
        _library.LibraryUpdated += (_, _) => RefreshFavoritesCount();
        _library.FavoritesChanged += (_, _) => RefreshFavoritesCount();
    }

    private bool _suppressNavigationRequest;

    partial void OnSelectedNavItemChanged(NavItem? oldValue, NavItem? newValue)
    {
        if (_suppressNavigationRequest) return;

        // Folder headers toggle expansion instead of navigating; restore the
        // previously selected item so the highlight doesn't move.
        // The toggle is deferred: this setter runs inside the ListBox's selection
        // commit, and rebuilding SidebarRows there makes Avalonia's SelectionModel
        // enumerate stale indices against the shrunken collection —
        // ArgumentOutOfRangeException, app crash (reported on Linux/X11).
        if (newValue is PlaylistNavItem { IsFolder: true } folder)
        {
            Dispatcher.UIThread.Post(() =>
            {
                ToggleFolderExpansion(folder.Label);
                SetSelectedNavItemSilently(oldValue);
            });
            return;
        }

        if (newValue != null)
            NavigationRequested?.Invoke(this, newValue.Key);
    }

    /// <summary>Collapses or expands a sidebar playlist folder and rebuilds the rows.
    /// Must never be called from inside a ListBox selection change (see above).</summary>
    public void ToggleFolderExpansion(string folderName)
    {
        if (!_collapsedFolders.Remove(folderName))
            _collapsedFolders.Add(folderName);
        RebuildSidebarRows();
    }

    /// <summary>Sets SelectedNavItem without firing NavigationRequested. Used to restore the
    /// highlighted item when a sidebar click triggers a modal (e.g. Settings) instead of a page nav.</summary>
    public void SetSelectedNavItemSilently(NavItem? value)
    {
        _suppressNavigationRequest = true;
        try { SelectedNavItem = value; }
        finally { _suppressNavigationRequest = false; }
    }

    /// <summary>Recalculates the number of favorited tracks off the UI thread.</summary>
    public void RefreshFavoritesCount()
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var count = _library.Tracks.Count(t => t.IsFavorite);
            Dispatcher.UIThread.Post(() => FavoritesCount = count);
        });
    }

    /// <summary>Loads playlists from persistence.</summary>
    public async Task LoadPlaylistsAsync()
    {
        var loadedPlaylists = await _persistence.LoadPlaylistsAsync();
        Playlists.Clear();
        PlaylistItems.Clear();

        foreach (var pl in loadedPlaylists)
        {
            Playlists.Add(pl);
            PlaylistItems.Add(BuildPlaylistNavItem(pl));
        }

        RebuildSidebarRows();
    }

    /// <summary>
    /// Rebuilds the flattened sidebar rows: pinned playlists, then folders
    /// (alphabetical) with their playlists when expanded, then loose playlists.
    /// </summary>
    public void RebuildSidebarRows()
    {
        SidebarRows.Clear();
        foreach (var row in BuildRows(PlaylistItems, _collapsedFolders))
            SidebarRows.Add(row);
    }

    /// <summary>
    /// Pure row-ordering logic, kept static for unit tests. Mutates IsInFolder
    /// on playlist items and synthesizes folder header rows.
    /// </summary>
    public static List<PlaylistNavItem> BuildRows(
        IEnumerable<PlaylistNavItem> items, ISet<string> collapsedFolders)
    {
        var rows = new List<PlaylistNavItem>();
        var all = items.ToList();

        foreach (var item in all.Where(i => i.IsPinned))
        {
            item.IsInFolder = false;
            rows.Add(item);
        }

        var unpinned = all.Where(i => !i.IsPinned).ToList();

        var folders = unpinned
            .Where(i => !string.IsNullOrWhiteSpace(i.Folder))
            .GroupBy(i => i.Folder.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in folders)
        {
            var expanded = !collapsedFolders.Contains(group.Key);
            rows.Add(new PlaylistNavItem
            {
                Key = $"folder:{group.Key}",
                Label = group.Key,
                IsFolder = true,
                IsExpanded = expanded,
                TrackCount = group.Count(),
            });

            if (!expanded) continue;
            foreach (var item in group)
            {
                item.IsInFolder = true;
                rows.Add(item);
            }
        }

        foreach (var item in unpinned.Where(i => string.IsNullOrWhiteSpace(i.Folder)))
        {
            item.IsInFolder = false;
            rows.Add(item);
        }

        return rows;
    }

    /// <summary>Existing folder names, for suggestions in the edit dialog.</summary>
    public IReadOnlyList<string> GetFolderNames() =>
        Playlists
            .Select(p => p.Folder.Trim())
            .Where(f => f.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Pins/unpins a playlist in the sidebar and persists.</summary>
    public async Task TogglePinAsync(Guid playlistId)
    {
        var playlist = Playlists.FirstOrDefault(p => p.Id == playlistId);
        if (playlist == null) return;

        playlist.IsPinned = !playlist.IsPinned;
        var navItem = PlaylistItems.FirstOrDefault(n => n.PlaylistId == playlistId);
        if (navItem != null) navItem.IsPinned = playlist.IsPinned;

        RebuildSidebarRows();
        await _persistence.SavePlaylistsAsync(Playlists.ToList());
    }

    /// <summary>Builds a PlaylistNavItem with resolved artwork for sidebar display.</summary>
    private PlaylistNavItem BuildPlaylistNavItem(Playlist pl)
    {
        var item = new PlaylistNavItem
        {
            Key = $"playlist:{pl.Id}",
            Label = pl.Name,
            IconGlyph = pl.IsSmartPlaylist ? "SmartPlaylistIcon" : "PlaylistsIcon",
            PlaylistId = pl.Id,
            TrackCount = pl.TrackIds.Count,
            CoverArtPath = pl.CoverArtPath,
            Color = pl.Color,
            IsPinned = pl.IsPinned,
            Folder = pl.Folder,
        };

        // "16 tracks · 54 min" meta line for the Playlists grid tiles
        var totalDuration = TimeSpan.Zero;
        foreach (var trackId in pl.TrackIds)
        {
            var t = _library.GetTrackById(trackId);
            if (t != null) totalDuration += t.Duration;
        }
        var tracksLabel = pl.TrackIds.Count == 1 ? "1 track" : $"{pl.TrackIds.Count:N0} tracks";
        var durationLabel = totalDuration.TotalHours >= 1
            ? $"{(int)totalDuration.TotalHours} hr {totalDuration.Minutes} min"
            : $"{(int)Math.Round(totalDuration.TotalMinutes)} min";
        item.MetaText = $"{tracksLabel} · {durationLabel}";

        // Resolve up to 4 unique album arts for collage thumbnail
        if (string.IsNullOrEmpty(pl.CoverArtPath))
        {
            var uniqueArts = new List<string>();
            var seenAlbums = new HashSet<Guid>();
            foreach (var trackId in pl.TrackIds)
            {
                if (uniqueArts.Count >= 4) break;
                var track = _library.GetTrackById(trackId);
                if (track?.AlbumArtworkPath != null && seenAlbums.Add(track.AlbumId))
                    uniqueArts.Add(track.AlbumArtworkPath);
            }
            if (uniqueArts.Count > 0) item.Art1 = uniqueArts[0];
            if (uniqueArts.Count > 1) item.Art2 = uniqueArts[1];
            if (uniqueArts.Count > 2) item.Art3 = uniqueArts[2];
            if (uniqueArts.Count > 3) item.Art4 = uniqueArts[3];
            // Fill empty collage cells so the 2x2 grid has no gaps
            if (uniqueArts.Count == 3) item.Art4 = uniqueArts[0];
            if (uniqueArts.Count == 2) { item.Art3 = uniqueArts[1]; item.Art4 = uniqueArts[0]; }
        }

        return item;
    }

    [RelayCommand]
    private async Task CreatePlaylist()
    {
        var dialogVm = new CreatePlaylistDialogViewModel();
        var dialog = new CreatePlaylistDialog
        {
            DataContext = dialogVm
        };

        bool playlistCreated = false;
        string playlistName = string.Empty;
        string playlistDescription = string.Empty;

        dialogVm.PlaylistCreated += (_, args) =>
        {
            playlistCreated = true;
            playlistName = args.Name;
            playlistDescription = args.Description;
        };

        dialogVm.CloseRequested += (_, _) => _ = dialog.CloseAnimatedAsync();

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is Window owner)
        {
            DialogHelper.SizeToOwner(dialog, owner);
            await dialog.ShowDialog(owner);
        }
        else
        {
            dialog.Show();
            return;
        }

        if (!playlistCreated)
            return;

        var playlist = new Playlist
        {
            Name = playlistName,
            Description = playlistDescription,
            Color = Playlist.GetRandomColor()
        };
        Playlists.Add(playlist);
        PlaylistItems.Add(BuildPlaylistNavItem(playlist));
        RebuildSidebarRows();

        await _persistence.SavePlaylistsAsync(Playlists.ToList());
    }

    [RelayCommand]
    private async Task DeletePlaylist(Guid playlistId)
    {
        var playlist = Playlists.FirstOrDefault(p => p.Id == playlistId);
        var name = playlist?.Name ?? "this playlist";
        var confirmed = await Views.ConfirmationDialog.ShowAsync($"Are you sure you want to delete \"{name}\"? This cannot be undone.");
        if (!confirmed) return;
        await DeletePlaylistAsync(playlistId);
    }

    /// <summary>Deletes a playlist by ID (public method for external calls).</summary>
    public async Task DeletePlaylistAsync(Guid playlistId)
    {
        var playlist = Playlists.FirstOrDefault(p => p.Id == playlistId);
        if (playlist == null) return;

        Playlists.Remove(playlist);

        var navItem = PlaylistItems.FirstOrDefault(n => n.PlaylistId == playlistId);
        if (navItem != null) PlaylistItems.Remove(navItem);
        RebuildSidebarRows();

        await _persistence.SavePlaylistsAsync(Playlists.ToList());
    }

    /// <summary>Renames a playlist and persists the change.</summary>
    public async Task RenamePlaylist(Guid playlistId, string newName)
    {
        var playlist = Playlists.FirstOrDefault(p => p.Id == playlistId);
        if (playlist == null) return;

        playlist.Name = newName;
        playlist.ModifiedAt = DateTime.UtcNow;

        var navItem = PlaylistItems.FirstOrDefault(n => n.PlaylistId == playlistId);
        if (navItem != null) navItem.Label = newName;

        await _persistence.SavePlaylistsAsync(Playlists.ToList());
    }

    /// <summary>Adds tracks to a playlist and persists.</summary>
    public async Task AddTracksToPlaylist(Guid playlistId, IEnumerable<Track> tracks)
    {
        var playlist = Playlists.FirstOrDefault(p => p.Id == playlistId);
        if (playlist == null) return;

        // Only add tracks that aren't already in the playlist to prevent duplicates
        var existingIds = new HashSet<Guid>(playlist.TrackIds);
        foreach (var track in tracks)
        {
            if (existingIds.Add(track.Id))
                playlist.TrackIds.Add(track.Id);
        }
        playlist.ModifiedAt = DateTime.UtcNow;

        // Update the sidebar item's track count and artwork
        var navItem = PlaylistItems.FirstOrDefault(n => n.PlaylistId == playlistId);
        if (navItem != null)
        {
            var rebuilt = BuildPlaylistNavItem(playlist);
            navItem.TrackCount = rebuilt.TrackCount;
            navItem.MetaText = rebuilt.MetaText;
            navItem.Art1 = rebuilt.Art1;
            navItem.Art2 = rebuilt.Art2;
            navItem.Art3 = rebuilt.Art3;
            navItem.Art4 = rebuilt.Art4;
        }

        await _persistence.SavePlaylistsAsync(Playlists.ToList());
        PlaylistTracksChanged?.Invoke(this, playlistId);
    }

    /// <summary>Opens the search-driven "Add Songs" picker for a manual playlist and
    /// appends the chosen tracks via <see cref="AddTracksToPlaylist"/>.</summary>
    public async Task OpenAddSongsAsync(Playlist playlist)
    {
        if (playlist == null || playlist.IsSmartPlaylist) return;

        var dialogVm = new AddSongsDialogViewModel(_library.Tracks.ToList(), playlist.TrackIds);
        var dialog = new AddSongsDialog { DataContext = dialogVm };

        IReadOnlyList<Track>? chosen = null;
        dialogVm.SongsChosen += (_, tracks) => chosen = tracks;
        dialogVm.CloseRequested += (_, _) => _ = dialog.CloseAnimatedAsync();

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is Window owner)
        {
            DialogHelper.SizeToOwner(dialog, owner);
            await dialog.ShowDialog(owner);
        }
        else
        {
            dialog.Show();
            return;
        }

        if (chosen != null && chosen.Count > 0)
            await AddTracksToPlaylist(playlist.Id, chosen);
    }

    /// <summary>
    /// Creates a manual playlist with the given name and tracks directly (no dialog),
    /// persists it, and returns it. Used by folder-import to keep a dropped collection
    /// (e.g. a downloaded playlist folder) together as a single playlist.
    /// </summary>
    public async Task<Playlist> CreatePlaylistFromTracksAsync(string name, IEnumerable<Track> tracks)
    {
        var playlist = new Playlist
        {
            Name = string.IsNullOrWhiteSpace(name) ? "New Playlist" : name.Trim(),
            Color = Playlist.GetRandomColor()
        };
        foreach (var t in tracks)
            playlist.TrackIds.Add(t.Id);

        Playlists.Add(playlist);
        PlaylistItems.Add(BuildPlaylistNavItem(playlist));
        RebuildSidebarRows();

        await _persistence.SavePlaylistsAsync(Playlists.ToList());
        return playlist;
    }

    /// <summary>True if a manual (non-smart) playlist with this name already exists.</summary>
    public bool ManualPlaylistExists(string name) =>
        Playlists.Any(p => !p.IsSmartPlaylist &&
                           string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Opens the unified "Add to Playlist" dialog for a single track.</summary>
    public Task CreatePlaylistWithTrackAsync(Track track)
        => OpenAddToPlaylistAsync(new List<Track> { track });

    /// <summary>Opens the unified "Add to Playlist" dialog for multiple tracks.</summary>
    public Task CreatePlaylistWithTracksAsync(IList<Track> tracks)
        => OpenAddToPlaylistAsync(tracks);

    /// <summary>
    /// Shows the combined "Add to Playlist" dialog: the user can pick an existing
    /// playlist (tracks added immediately) or inline-create a new one (tracks added
    /// to the new playlist on creation).
    /// </summary>
    public async Task OpenAddToPlaylistAsync(IList<Track> tracks)
    {
        if (tracks == null || tracks.Count == 0) return;

        var dialogVm = new AddToPlaylistDialogViewModel(PlaylistItems, tracks.Count);
        var dialog = new AddToPlaylistDialog { DataContext = dialogVm };

        Guid? selectedExistingId = null;
        bool createRequested = false;
        string newName = string.Empty;
        string newDescription = string.Empty;

        dialogVm.PlaylistSelected += (_, navItem) => selectedExistingId = navItem.PlaylistId;
        dialogVm.NewPlaylistRequested += (_, args) =>
        {
            createRequested = true;
            newName = args.Name;
            newDescription = args.Description;
        };
        dialogVm.CloseRequested += (_, _) => _ = dialog.CloseAnimatedAsync();

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is Window owner)
        {
            DialogHelper.SizeToOwner(dialog, owner);
            await dialog.ShowDialog(owner);
        }
        else
        {
            dialog.Show();
            return;
        }

        if (selectedExistingId is Guid id)
        {
            await AddTracksToPlaylist(id, tracks);
            return;
        }

        if (!createRequested) return;

        var playlist = new Playlist
        {
            Name = newName,
            Description = newDescription,
            Color = Playlist.GetRandomColor()
        };
        foreach (var t in tracks)
            playlist.TrackIds.Add(t.Id);

        Playlists.Add(playlist);
        PlaylistItems.Add(BuildPlaylistNavItem(playlist));
        RebuildSidebarRows();

        await _persistence.SavePlaylistsAsync(Playlists.ToList());
    }

    /// <summary>Opens the edit playlist dialog pre-filled with existing data and saves changes.</summary>
    public async Task EditPlaylistAsync(Playlist playlist)
    {
        var currentNavItem = PlaylistItems.FirstOrDefault(n => n.PlaylistId == playlist.Id);
        var dialogVm = new EditPlaylistDialogViewModel
        {
            PlaylistName = playlist.Name,
            PlaylistDescription = playlist.Description,
            PlaylistColor = playlist.Color,
            CoverArtPath = playlist.CoverArtPath,
            Art1 = currentNavItem?.Art1,
            Art2 = currentNavItem?.Art2,
            Art3 = currentNavItem?.Art3,
            Art4 = currentNavItem?.Art4,
            IsPinned = playlist.IsPinned,
            PlaylistFolder = playlist.Folder,
            ExistingFoldersHint = string.Join(", ", GetFolderNames()),
            ExistingFolders = GetFolderNames(),
        };
        var dialog = new EditPlaylistDialog { DataContext = dialogVm };

        bool saved = false;
        string newName = string.Empty;
        string newDescription = string.Empty;

        dialogVm.PlaylistSaved += (_, args) =>
        {
            saved = true;
            newName = args.Name;
            newDescription = args.Description;
        };

        dialogVm.CloseRequested += (_, _) => _ = dialog.CloseAnimatedAsync();

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is Window owner)
        {
            DialogHelper.SizeToOwner(dialog, owner);
            await dialog.ShowDialog(owner);
        }
        else
        {
            dialog.Show();
            return;
        }

        if (!saved) return;

        playlist.Name = newName;
        playlist.Description = newDescription;
        playlist.IsPinned = dialogVm.IsPinned;
        playlist.Folder = dialogVm.PlaylistFolder.Trim();
        playlist.ModifiedAt = DateTime.UtcNow;

        // Handle cover art changes
        if (dialogVm.CoverArtRemoved)
        {
            if (!string.IsNullOrEmpty(playlist.CoverArtPath) && File.Exists(playlist.CoverArtPath))
            {
                try { File.Delete(playlist.CoverArtPath); } catch { /* non-fatal */ }
            }
            playlist.CoverArtPath = null;
        }
        else if (!string.IsNullOrEmpty(dialogVm.PendingCoverArtFile))
        {
            var coversDir = Path.Combine(Helpers.AppPaths.DataRoot, "playlist_covers");
            Directory.CreateDirectory(coversDir);

            var ext = Path.GetExtension(dialogVm.PendingCoverArtFile);
            var destPath = Path.Combine(coversDir, $"{playlist.Id}{ext}");
            File.Copy(dialogVm.PendingCoverArtFile, destPath, overwrite: true);
            playlist.CoverArtPath = destPath;
        }

        // Rebuild the sidebar nav item with updated info
        var navItem = PlaylistItems.FirstOrDefault(n => n.PlaylistId == playlist.Id);
        if (navItem != null)
        {
            var rebuilt = BuildPlaylistNavItem(playlist);
            navItem.Label = newName;
            navItem.TrackCount = rebuilt.TrackCount;
            navItem.MetaText = rebuilt.MetaText;
            navItem.CoverArtPath = rebuilt.CoverArtPath;
            navItem.Art1 = rebuilt.Art1;
            navItem.Art2 = rebuilt.Art2;
            navItem.Art3 = rebuilt.Art3;
            navItem.Art4 = rebuilt.Art4;
            navItem.IsPinned = rebuilt.IsPinned;
            navItem.Folder = rebuilt.Folder;
        }

        RebuildSidebarRows();
        await _persistence.SavePlaylistsAsync(Playlists.ToList());
    }

    /// <summary>Gets a playlist by its ID.</summary>
    public Playlist? GetPlaylist(Guid id) => Playlists.FirstOrDefault(p => p.Id == id);

    /// <summary>Opens the smart playlist creation dialog.</summary>
    public async Task CreateSmartPlaylistAsync()
    {
        var dialogVm = new CreateSmartPlaylistDialogViewModel(_library);
        var dialog = new CreateSmartPlaylistDialog { DataContext = dialogVm };

        Playlist? createdPlaylist = null;

        dialogVm.SmartPlaylistCreated += (_, playlist) =>
        {
            createdPlaylist = playlist;
        };

        dialogVm.CloseRequested += (_, _) => _ = dialog.CloseAnimatedAsync();

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is Window owner)
        {
            DialogHelper.SizeToOwner(dialog, owner);
            await dialog.ShowDialog(owner);
        }
        else
        {
            dialog.Show();
            return;
        }

        if (createdPlaylist == null) return;

        Playlists.Add(createdPlaylist);
        PlaylistItems.Add(BuildPlaylistNavItem(createdPlaylist));
        RebuildSidebarRows();

        await _persistence.SavePlaylistsAsync(Playlists.ToList());
    }
}
