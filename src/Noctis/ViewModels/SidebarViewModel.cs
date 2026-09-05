using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Localization;
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

    /// <summary>Main navigation items (Home, Songs, Albums, Artists, Folders, Playlists, Visualizer, Settings).</summary>
    public ObservableCollection<NavItem> NavItems { get; } = new()
    {
        new NavItem { Key = "home", Label = Loc.T("Nav.Home"), IconGlyph = "HomeIcon" },
        new NavItem { Key = "songs", Label = Loc.T("Nav.Songs"), IconGlyph = "SongsIcon" },
        new NavItem { Key = "albums", Label = Loc.T("Nav.Albums"), IconGlyph = "AlbumsIcon" },
        new NavItem { Key = "artists", Label = Loc.T("Nav.Artists"), IconGlyph = "ArtistsIcon" },
        new NavItem { Key = "folders", Label = Loc.T("Nav.Folders"), IconGlyph = "FoldersIcon" },
        new NavItem { Key = "playlists", Label = Loc.T("Nav.Playlists"), IconGlyph = "PlaylistsIcon" },
        new NavItem { Key = "visualizer", Label = Loc.T("Nav.Visualizer"), IconGlyph = "VisualizerIcon" },
        new NavItem { Key = "settings", Label = Loc.T("Nav.Settings"), IconGlyph = "SettingsIcon" },
    };

    /// <summary>Favorites navigation item (below divider).</summary>
    public ObservableCollection<NavItem> FavoritesItems { get; } = new()
    {
        new NavItem { Key = "favorites", Label = Loc.T("Nav.Favorites"), IconGlyph = "FavoritesIcon" },
    };

    /// <summary>Resource key for a section's label, by nav key.</summary>
    public static string LabelKey(string navKey) => navKey switch
    {
        "home" => "Nav.Home", "songs" => "Nav.Songs", "albums" => "Nav.Albums", "artists" => "Nav.Artists",
        "folders" => "Nav.Folders", "playlists" => "Nav.Playlists", "visualizer" => "Nav.Visualizer",
        "settings" => "Nav.Settings", "favorites" => "Nav.Favorites", "server" => "Nav.Server", "cd" => "Nav.AudioCd",
        _ => navKey,
    };

    /// <summary>Re-labels the section items after a language switch (labels are plain strings, not bindings).</summary>
    private void RelabelSections()
    {
        foreach (var item in NavItems.Concat(FavoritesItems))
            item.Label = Loc.T(LabelKey(item.Key));
    }

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
        Loc.Instance.CultureChanged += (_, _) => RelabelSections();
    }

    /// <summary>
    /// Shows or hides the "Server" entry (between Playlists and Settings). The item
    /// is inserted/removed rather than IsVisible-toggled so the collapsed ListBoxItem
    /// container doesn't leave a dead gap in the rail.
    /// </summary>
    public void SetServerSectionVisible(bool visible)
        => SetOptionalSectionVisible(visible, "server", Loc.T("Nav.Server"), "ServerIcon", after: null, before: "cd");

    /// <summary>
    /// Shows or hides the "Audio CD" entry. It sits after Server (when present),
    /// otherwise directly above Settings, and only exists while an optical drive does.
    /// </summary>
    public void SetAudioCdSectionVisible(bool visible)
        => SetOptionalSectionVisible(visible, "cd", Loc.T("Nav.AudioCd"), "CdIcon", after: "server", before: null);

    /// <summary>Insert order: right after <paramref name="after"/> if present, else right before
    /// <paramref name="before"/> if present, else above Settings.</summary>
    private void SetOptionalSectionVisible(bool visible, string key, string label, string icon, string? after, string? before)
    {
        var existing = NavItems.FirstOrDefault(i => i.Key == key);
        if (visible == (existing != null)) return;
        if (!visible) { NavItems.Remove(existing!); return; }

        var indexed = NavItems.Select((item, index) => (item, index)).ToList();
        var afterIndex = after == null ? -1 : indexed.FirstOrDefault(x => x.item.Key == after, (null!, -1)).index;
        var beforeIndex = before == null ? -1 : indexed.FirstOrDefault(x => x.item.Key == before, (null!, -1)).index;
        var settingsIndex = indexed.FirstOrDefault(x => x.item.Key == "settings", (null!, -1)).index;
        var insertAt = afterIndex >= 0 ? afterIndex + 1
            : beforeIndex >= 0 ? beforeIndex
            : settingsIndex >= 0 ? settingsIndex
            : NavItems.Count;
        NavItems.Insert(insertAt, new NavItem { Key = key, Label = label, IconGlyph = icon });
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

    /// <summary>Fires NavigationRequested for the already-selected item. The nav ListBoxes
    /// only raise SelectionChanged when the selection actually changes, so a click on the
    /// item that is already highlighted (e.g. Home while inside an album opened from Home)
    /// never reaches OnSelectedNavItemChanged — the view routes those clicks here.</summary>
    public void RequestNavigation(NavItem item) => NavigationRequested?.Invoke(this, item.Key);

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
    /// Syncs the collection in place instead of Clear+refill: tearing down the
    /// row under the pointer mid-click dropped the sidebar wrapper's
    /// IsPointerOver for a frame, which the hover-expand handler in MainWindow
    /// read as the cursor leaving — the rail snapped shut and reopened on
    /// every folder toggle.
    /// </summary>
    public void RebuildSidebarRows()
    {
        var desired = BuildRows(PlaylistItems, _collapsedFolders);

        // Folder headers are synthesized fresh by BuildRows; swap in the live
        // instances (matched by key) so their ListBox containers survive.
        for (int i = 0; i < desired.Count; i++)
        {
            if (!desired[i].IsFolder) continue;
            var existing = SidebarRows.FirstOrDefault(r =>
                r.IsFolder && string.Equals(r.Key, desired[i].Key, StringComparison.OrdinalIgnoreCase));
            if (existing == null) continue;
            existing.Label = desired[i].Label;
            existing.IsExpanded = desired[i].IsExpanded;
            existing.TrackCount = desired[i].TrackCount;
            desired[i] = existing;
        }

        // Minimal moves/inserts; desired rows are distinct, so a row not yet
        // placed sits at an index >= i (or is absent). Excess rows fall off the end.
        for (int i = 0; i < desired.Count; i++)
        {
            var at = SidebarRows.IndexOf(desired[i]);
            if (at == i) continue;
            if (at > i) SidebarRows.Move(at, i);
            else SidebarRows.Insert(i, desired[i]);
        }
        while (SidebarRows.Count > desired.Count)
            SidebarRows.RemoveAt(SidebarRows.Count - 1);
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
            IsSmartPlaylist = pl.IsSmartPlaylist,
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
    private Task CreatePlaylist() => CreatePlaylistCoreAsync(folder: null);

    /// <summary>Folder header context menu: "New Playlist in folder" lands inside that folder.</summary>
    [RelayCommand]
    private Task NewPlaylistInFolder(PlaylistNavItem? item)
        => CreatePlaylistCoreAsync(item is { IsFolder: true } ? item.Label : null);

    /// <summary>Sidebar header context menu: same dialog the Playlists page "New" menu opens.</summary>
    [RelayCommand]
    private Task CreateSmartPlaylistFromSidebar() => CreateSmartPlaylistAsync();

    private async Task CreatePlaylistCoreAsync(string? folder)
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
            Color = Playlist.GetRandomColor(),
            Folder = folder?.Trim() ?? string.Empty,
        };
        Playlists.Add(playlist);
        PlaylistItems.Add(BuildPlaylistNavItem(playlist));
        RebuildSidebarRows();

        await _persistence.SavePlaylistsAsync(Playlists.ToList());
    }

    // ── Sidebar context menu + drag-and-drop ──
    // Right-click on a playlist row / folder header, and drops onto the list, come
    // through here. Folders are just the Folder string on each playlist, so "create a
    // folder" means "put a playlist in a folder with a new name" and a folder with no
    // playlists left in it simply disappears.

    [RelayCommand]
    private async Task EditPlaylistItem(PlaylistNavItem? item)
    {
        var playlist = ResolveNavPlaylist(item);
        if (playlist == null) return;
        await EditPlaylistAsync(playlist);
    }

    [RelayCommand]
    private async Task TogglePinItem(PlaylistNavItem? item)
    {
        if (item?.PlaylistId is { } id) await TogglePinAsync(id);
    }

    [RelayCommand]
    private async Task DeletePlaylistItem(PlaylistNavItem? item)
    {
        if (item?.PlaylistId is { } id) await DeletePlaylist(id);
    }

    /// <summary>Prompts for a folder name (existing or new) and moves the playlist there.</summary>
    [RelayCommand]
    private async Task MoveToFolder(PlaylistNavItem? item)
    {
        var playlist = ResolveNavPlaylist(item);
        if (playlist == null) return;

        var existing = GetFolderNames();
        var hint = existing.Count > 0
            ? "Existing folders: " + string.Join(", ", existing) + ". Type a new name to create a folder."
            : "Type a name to create a folder.";
        var name = await Views.TextPromptDialog.ShowAsync("Move to folder", playlist.Folder, hint, "Move");
        if (name == null) return;

        await SetPlaylistFolderAsync(playlist, name);
    }

    [RelayCommand]
    private async Task RemoveFromFolder(PlaylistNavItem? item)
    {
        var playlist = ResolveNavPlaylist(item);
        if (playlist == null || string.IsNullOrWhiteSpace(playlist.Folder)) return;
        await SetPlaylistFolderAsync(playlist, string.Empty);
    }

    /// <summary>Folder header: rename every playlist's Folder that matches.</summary>
    [RelayCommand]
    private async Task RenameFolder(PlaylistNavItem? item)
    {
        if (item is not { IsFolder: true }) return;
        var newName = await Views.TextPromptDialog.ShowAsync("Rename folder", item.Label, null, "Rename");
        if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName, item.Label, StringComparison.Ordinal)) return;

        var wasCollapsed = _collapsedFolders.Remove(item.Label);
        if (wasCollapsed) _collapsedFolders.Add(newName);

        foreach (var pl in Playlists.Where(p => string.Equals(p.Folder.Trim(), item.Label, StringComparison.OrdinalIgnoreCase)))
        {
            pl.Folder = newName;
            pl.ModifiedAt = DateTime.UtcNow;
            var nav = PlaylistItems.FirstOrDefault(n => n.PlaylistId == pl.Id);
            if (nav != null) nav.Folder = newName;
        }
        RebuildSidebarRows();
        await _persistence.SavePlaylistsAsync(Playlists.ToList());
    }

    /// <summary>Folder header: the playlists stay, the folder goes.</summary>
    [RelayCommand]
    private async Task DissolveFolder(PlaylistNavItem? item)
    {
        if (item is not { IsFolder: true }) return;
        var confirmed = await Views.ConfirmationDialog.ShowAsync(
            $"Remove the folder \"{item.Label}\"? The playlists inside it are kept.");
        if (!confirmed) return;

        _collapsedFolders.Remove(item.Label);
        foreach (var pl in Playlists.Where(p => string.Equals(p.Folder.Trim(), item.Label, StringComparison.OrdinalIgnoreCase)))
        {
            pl.Folder = string.Empty;
            pl.ModifiedAt = DateTime.UtcNow;
            var nav = PlaylistItems.FirstOrDefault(n => n.PlaylistId == pl.Id);
            if (nav != null) nav.Folder = string.Empty;
        }
        RebuildSidebarRows();
        await _persistence.SavePlaylistsAsync(Playlists.ToList());
    }

    /// <summary>
    /// Drop of a dragged playlist onto <paramref name="target"/>: onto a folder header
    /// files it into that folder (end of the folder); onto another playlist places it
    /// right before/after that row and adopts the row's group (pinned state + folder),
    /// so "put it where I dropped it" is exactly what happens. Sidebar order within a
    /// group is the saved playlist order.
    /// </summary>
    public async Task MovePlaylistAsync(Guid draggedId, PlaylistNavItem target, bool placeAfter)
    {
        var dragged = Playlists.FirstOrDefault(p => p.Id == draggedId);
        if (dragged == null) return;

        if (target.IsFolder)
        {
            dragged.IsPinned = false;
            dragged.Folder = target.Label;
            Playlists.Remove(dragged);
            var lastInFolder = Playlists.LastOrDefault(p =>
                !p.IsPinned && string.Equals(p.Folder.Trim(), target.Label, StringComparison.OrdinalIgnoreCase));
            var insertAt = lastInFolder == null ? Playlists.Count : Playlists.IndexOf(lastInFolder) + 1;
            Playlists.Insert(insertAt, dragged);
        }
        else
        {
            if (target.PlaylistId == null || target.PlaylistId == draggedId) return;
            var targetPl = Playlists.FirstOrDefault(p => p.Id == target.PlaylistId);
            if (targetPl == null) return;

            dragged.IsPinned = targetPl.IsPinned;
            dragged.Folder = targetPl.Folder;
            Playlists.Remove(dragged);
            var idx = Playlists.IndexOf(targetPl) + (placeAfter ? 1 : 0);
            Playlists.Insert(Math.Clamp(idx, 0, Playlists.Count), dragged);
        }

        dragged.ModifiedAt = DateTime.UtcNow;
        SyncPlaylistItemsWithPlaylists();
        RebuildSidebarRows();
        await _persistence.SavePlaylistsAsync(Playlists.ToList());
    }

    private Playlist? ResolveNavPlaylist(PlaylistNavItem? item)
        => item?.PlaylistId is { } id ? Playlists.FirstOrDefault(p => p.Id == id) : null;

    private async Task SetPlaylistFolderAsync(Playlist playlist, string folder)
    {
        playlist.Folder = folder.Trim();
        playlist.ModifiedAt = DateTime.UtcNow;
        var nav = PlaylistItems.FirstOrDefault(n => n.PlaylistId == playlist.Id);
        if (nav != null) nav.Folder = playlist.Folder;
        RebuildSidebarRows();
        await _persistence.SavePlaylistsAsync(Playlists.ToList());
    }

    /// <summary>Re-orders PlaylistItems to match Playlists and copies pin/folder state across.</summary>
    private void SyncPlaylistItemsWithPlaylists()
    {
        for (int i = 0; i < Playlists.Count; i++)
        {
            var pl = Playlists[i];
            var nav = PlaylistItems.FirstOrDefault(n => n.PlaylistId == pl.Id);
            if (nav == null) continue;
            nav.IsPinned = pl.IsPinned;
            nav.Folder = pl.Folder;
            var at = PlaylistItems.IndexOf(nav);
            if (at != i && i < PlaylistItems.Count) PlaylistItems.Move(at, i);
        }
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

            // Drop a previous cover with a different extension: replacing a .png with a
            // .jpg left the .png behind forever, since RemoveCoverArt only deletes the
            // path currently recorded.
            foreach (var stale in Directory.EnumerateFiles(coversDir, $"{playlist.Id}.*"))
            {
                if (!string.Equals(stale, destPath, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(stale); } catch { }
            }

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
