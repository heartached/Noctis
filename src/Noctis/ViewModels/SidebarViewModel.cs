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

    private const int MaxVisiblePlaylists = 12;

    [ObservableProperty] private NavItem? _selectedNavItem;
    [ObservableProperty] private bool _showShowAll;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private int _favoritesCount;

    /// <summary>Playlists visible in the sidebar (capped to 10).</summary>
    public ObservableCollection<PlaylistNavItem> VisiblePlaylistItems { get; } = new();

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

    partial void OnSelectedNavItemChanged(NavItem? value)
    {
        if (value != null)
            NavigationRequested?.Invoke(this, value.Key);
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

        RefreshVisiblePlaylists();
    }

    /// <summary>Rebuilds the visible playlist list (capped to 10).</summary>
    public void RefreshVisiblePlaylists()
    {
        VisiblePlaylistItems.Clear();
        foreach (var item in PlaylistItems.Take(MaxVisiblePlaylists))
            VisiblePlaylistItems.Add(item);

        ShowShowAll = PlaylistItems.Count > MaxVisiblePlaylists;
    }

    [RelayCommand]
    private void ShowAllPlaylists()
    {
        SelectedNavItem = NavItems.First(n => n.Key == "playlists");
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
        };

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

        dialogVm.CloseRequested += (_, _) => dialog.Close();

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
        RefreshVisiblePlaylists();

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
        RefreshVisiblePlaylists();

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
            navItem.Art1 = rebuilt.Art1;
            navItem.Art2 = rebuilt.Art2;
            navItem.Art3 = rebuilt.Art3;
            navItem.Art4 = rebuilt.Art4;
        }

        await _persistence.SavePlaylistsAsync(Playlists.ToList());
        PlaylistTracksChanged?.Invoke(this, playlistId);
    }

    /// <summary>Creates a new playlist containing a single track.</summary>
    public async Task CreatePlaylistWithTrackAsync(Track track)
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

        dialogVm.CloseRequested += (_, _) => dialog.Close();

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
        playlist.TrackIds.Add(track.Id);
        Playlists.Add(playlist);
        PlaylistItems.Add(BuildPlaylistNavItem(playlist));
        RefreshVisiblePlaylists();

        await _persistence.SavePlaylistsAsync(Playlists.ToList());
    }

    /// <summary>Creates a new playlist containing multiple tracks.</summary>
    public async Task CreatePlaylistWithTracksAsync(IList<Track> tracks)
    {
        if (tracks.Count == 0) return;

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

        dialogVm.CloseRequested += (_, _) => dialog.Close();

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
        foreach (var track in tracks)
        {
            playlist.TrackIds.Add(track.Id);
        }
        Playlists.Add(playlist);
        PlaylistItems.Add(BuildPlaylistNavItem(playlist));
        RefreshVisiblePlaylists();

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
            Art4 = currentNavItem?.Art4
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

        dialogVm.CloseRequested += (_, _) => dialog.Close();

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
            navItem.CoverArtPath = rebuilt.CoverArtPath;
            navItem.Art1 = rebuilt.Art1;
            navItem.Art2 = rebuilt.Art2;
            navItem.Art3 = rebuilt.Art3;
            navItem.Art4 = rebuilt.Art4;
        }

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

        dialogVm.CloseRequested += (_, _) => dialog.Close();

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
        RefreshVisiblePlaylists();

        await _persistence.SavePlaylistsAsync(Playlists.ToList());
    }
}
