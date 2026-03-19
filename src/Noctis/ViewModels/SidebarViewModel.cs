using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
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

    /// <summary>Home navigation item.</summary>
    public ObservableCollection<NavItem> HomeItems { get; } = new()
    {
        new NavItem { Key = "home", Label = "Home", IconGlyph = "HomeIcon" },
    };

    /// <summary>Fixed library navigation items.</summary>
    public ObservableCollection<NavItem> LibraryItems { get; } = new()
    {
        new NavItem { Key = "songs", Label = "Songs", IconGlyph = "SongsIcon" },
        new NavItem { Key = "albums", Label = "Albums", IconGlyph = "AlbumsIcon" },
        new NavItem { Key = "artists", Label = "Artists", IconGlyph = "ArtistsIcon" },
        new NavItem { Key = "genres", Label = "Genres", IconGlyph = "GenresIcon" },
        new NavItem { Key = "playlists", Label = "Playlists", IconGlyph = "PlaylistsIcon" },
    };

    /// <summary>Favorites navigation items.</summary>
    public ObservableCollection<NavItem> FavoritesItems { get; } = new()
    {
        new NavItem { Key = "favorites", Label = "Your Favorites", IconGlyph = "FavoritesIcon" },
    };

    /// <summary>User-created playlists (hidden from sidebar, shown in playlists view).</summary>
    public ObservableCollection<NavItem> PlaylistItems { get; } = new();

    /// <summary>System navigation items (settings).</summary>
    public ObservableCollection<NavItem> SystemItems { get; } = new()
    {
        new NavItem { Key = "settings", Label = "Settings", IconGlyph = "SettingsIcon" },
    };

    /// <summary>The underlying playlist models as observable collection.</summary>
    public ObservableCollection<Playlist> Playlists { get; } = new();

    /// <summary>Fires when the user selects a different navigation item.</summary>
    public event EventHandler<string>? NavigationRequested;

    public SidebarViewModel(IPersistenceService persistence, ILibraryService library)
    {
        _persistence = persistence;
        _library = library;
    }

    partial void OnSelectedNavItemChanged(NavItem? value)
    {
        if (value != null)
            NavigationRequested?.Invoke(this, value.Key);
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

            PlaylistItems.Add(new NavItem
            {
                Key = $"playlist:{pl.Id}",
                Label = pl.Name,
                IconGlyph = pl.IsSmartPlaylist ? "SmartPlaylistIcon" : "PlaylistsIcon",
                PlaylistId = pl.Id
            });
        }
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
            && desktop.MainWindow != null)
        {
            await dialog.ShowDialog(desktop.MainWindow);
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

        PlaylistItems.Add(new NavItem
        {
            Key = $"playlist:{playlist.Id}",
            Label = playlist.Name,
            IconGlyph = "PlaylistsIcon",
            PlaylistId = playlist.Id
        });

        await _persistence.SavePlaylistsAsync(Playlists.ToList());
    }

    [RelayCommand]
    private async Task DeletePlaylist(Guid playlistId)
    {
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

        await _persistence.SavePlaylistsAsync(Playlists.ToList());
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
            && desktop.MainWindow != null)
        {
            await dialog.ShowDialog(desktop.MainWindow);
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

        PlaylistItems.Add(new NavItem
        {
            Key = $"playlist:{playlist.Id}",
            Label = playlist.Name,
            IconGlyph = "PlaylistsIcon",
            PlaylistId = playlist.Id
        });

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
            && desktop.MainWindow != null)
        {
            await dialog.ShowDialog(desktop.MainWindow);
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

        PlaylistItems.Add(new NavItem
        {
            Key = $"playlist:{playlist.Id}",
            Label = playlist.Name,
            IconGlyph = "PlaylistsIcon",
            PlaylistId = playlist.Id
        });

        await _persistence.SavePlaylistsAsync(Playlists.ToList());
    }

    /// <summary>Opens the edit playlist dialog pre-filled with existing data and saves changes.</summary>
    public async Task EditPlaylistAsync(Playlist playlist)
    {
        var dialogVm = new EditPlaylistDialogViewModel
        {
            PlaylistName = playlist.Name,
            PlaylistDescription = playlist.Description,
            PlaylistColor = playlist.Color,
            CoverArtPath = playlist.CoverArtPath
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
            && desktop.MainWindow != null)
        {
            await dialog.ShowDialog(desktop.MainWindow);
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
            var coversDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Noctis", "playlist_covers");
            Directory.CreateDirectory(coversDir);

            var ext = Path.GetExtension(dialogVm.PendingCoverArtFile);
            var destPath = Path.Combine(coversDir, $"{playlist.Id}{ext}");
            File.Copy(dialogVm.PendingCoverArtFile, destPath, overwrite: true);
            playlist.CoverArtPath = destPath;
        }

        var navItem = PlaylistItems.FirstOrDefault(n => n.PlaylistId == playlist.Id);
        if (navItem != null) navItem.Label = newName;

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
            && desktop.MainWindow != null)
        {
            await dialog.ShowDialog(desktop.MainWindow);
        }
        else
        {
            dialog.Show();
            return;
        }

        if (createdPlaylist == null) return;

        Playlists.Add(createdPlaylist);

        PlaylistItems.Add(new NavItem
        {
            Key = $"playlist:{createdPlaylist.Id}",
            Label = createdPlaylist.Name,
            IconGlyph = "SmartPlaylistIcon",
            PlaylistId = createdPlaylist.Id
        });

        await _persistence.SavePlaylistsAsync(Playlists.ToList());
    }
}
