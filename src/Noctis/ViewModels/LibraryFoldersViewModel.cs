using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// ViewModel for the "Folders" library view — browses tracks by on-disk folder hierarchy
/// under the user's configured music roots.
/// </summary>
public partial class LibraryFoldersViewModel : ViewModelBase, ISearchable, IDisposable
{
    private readonly ILibraryService _library;
    private readonly PlayerViewModel _player;
    private readonly IPersistenceService _persistence;
    private readonly SidebarViewModel _sidebar;

    private EventHandler? _libraryUpdatedHandler;
    private bool _isDirty = true;
    private string _currentFilter = string.Empty;

    /// <summary>Root nodes of the folder forest (one per configured music root).</summary>
    public ObservableCollection<FolderNode> RootNodes { get; } = new();

    /// <summary>Tracks to display in the right-hand pane (direct + descendant tracks of SelectedNode).</summary>
    public BulkObservableCollection<Track> SelectedFolderTracks { get; } = new();

    /// <summary>Exposed so row templates can drive the playing-track EQ visualizer off player state.</summary>
    public PlayerViewModel Player => _player;

    /// <summary>Track-list scroll offset saved when the view detaches (e.g. opening an
    /// artist/album page from a row link) and restored when it re-attaches on Back.</summary>
    public double SavedScrollOffset { get; set; }

    [ObservableProperty] private FolderNode? _selectedNode;

    /// <summary>Fires when the user clicks "Manage media folders…" — handled by MainWindowViewModel to switch views.</summary>
    public event EventHandler? NavigateToSettingsRequested;

    public LibraryFoldersViewModel(ILibraryService library, PlayerViewModel player, IPersistenceService persistence, SidebarViewModel sidebar)
    {
        _library = library;
        _player = player;
        _persistence = persistence;
        _sidebar = sidebar;

        _libraryUpdatedHandler = (_, _) =>
        {
            _isDirty = true;
            Dispatcher.UIThread.Post(Refresh);
        };
        _library.LibraryUpdated += _libraryUpdatedHandler;
    }

    /// <summary>Forces the next Refresh() call to rebuild even if data hasn't changed.</summary>
    public void MarkDirty() => _isDirty = true;

    /// <summary>Non-blocking refresh — loads settings, rebuilds forest, updates collections on UI thread.</summary>
    public async void Refresh()
    {
        // async void: never let an exception escape to the synchronization context
        // (an unhandled one here can terminate the app), so log and swallow instead.
        try
        {
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FoldersVM] Refresh failed: {ex.Message}");
        }
    }

    private async Task RefreshAsync()
    {
        if (!_isDirty && RootNodes.Count > 0)
            return;
        _isDirty = false;

        var settings = await _persistence.LoadSettingsAsync();
        var roots = settings.MusicFolders;
        var tracks = _library.Tracks.ToList();

        // Rebuilds create fresh FolderNode instances, so carry expansion over
        // by folder path or the tree collapses on every library update.
        var expansion = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in RootNodes)
            CaptureExpansion(root, expansion);

        var forest = await Task.Run(() => FolderTreeBuilder.Build(tracks, roots));

        foreach (var root in forest)
            RestoreExpansion(root, expansion);

        RootNodes.Clear();
        foreach (var root in forest)
            RootNodes.Add(root);

        var keep = SelectedNode;
        if (keep == null || !ContainsNode(forest, keep.FullPath))
        {
            SelectedNode = null;
            SelectedFolderTracks.ReplaceAll(Array.Empty<Track>());
        }
        else
        {
            // Re-run selection handler to refresh the track pane against the new forest.
            OnSelectedNodeChanged(SelectedNode);
        }
    }

    partial void OnSelectedNodeChanged(FolderNode? value) => RebuildTrackPane();

    /// <summary>Applies the top-bar search filter ("Find in Folders"). Empty clears it.</summary>
    public void ApplyFilter(string query)
    {
        _currentFilter = query ?? string.Empty;
        RebuildTrackPane();
    }

    private void RebuildTrackPane()
    {
        var hasFilter = !string.IsNullOrWhiteSpace(_currentFilter);
        var sink = new List<Track>();

        if (SelectedNode != null)
            Collect(SelectedNode, sink);
        else if (hasFilter)
            // No folder selected: search across all roots so the box still works.
            foreach (var root in RootNodes)
                Collect(root, sink);

        if (hasFilter)
        {
            var q = _currentFilter.Trim();
            sink = sink.Where(t =>
                (t.Title?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (t.Artist?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (t.Album?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        }

        // Assign 1-based list positions for the leading row-number column.
        for (int i = 0; i < sink.Count; i++)
            sink[i].RowNumber = i + 1;

        SelectedFolderTracks.ReplaceAll(sink);
    }

    private static void Collect(FolderNode node, List<Track> sink)
    {
        sink.AddRange(node.DirectTracks);
        foreach (var child in node.Children)
            Collect(child, sink);
    }

    private static void CaptureExpansion(FolderNode node, Dictionary<string, bool> map)
    {
        map[node.FullPath] = node.IsExpanded;
        foreach (var child in node.Children)
            CaptureExpansion(child, map);
    }

    private static void RestoreExpansion(FolderNode node, Dictionary<string, bool> map)
    {
        if (map.TryGetValue(node.FullPath, out var isExpanded))
            node.IsExpanded = isExpanded;
        foreach (var child in node.Children)
            RestoreExpansion(child, map);
    }

    private static bool ContainsNode(IReadOnlyList<FolderNode> forest, string fullPath)
    {
        foreach (var n in forest)
        {
            if (string.Equals(n.FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
                return true;
            if (ContainsNode(n.Children.ToList(), fullPath))
                return true;
        }
        return false;
    }

    [RelayCommand]
    private void PlayFolder()
    {
        if (SelectedFolderTracks.Count == 0) return;
        _player.ReplaceQueueAndPlay(SelectedFolderTracks.ToList(), 0);
    }

    [RelayCommand]
    private void ShuffleFolder()
    {
        if (SelectedFolderTracks.Count == 0) return;
        var shuffled = Helpers.ShuffleHelper.WeightedShuffle(SelectedFolderTracks);
        _player.ReplaceQueueAndPlay(shuffled, 0);
    }

    [RelayCommand]
    private void OpenMediaFolderSettings()
    {
        NavigateToSettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void PlayTrack(Track track)
    {
        var list = SelectedFolderTracks.ToList();
        var index = list.IndexOf(track);
        if (index < 0) index = 0;
        _player.ReplaceQueueAndPlay(list, index);
    }

    // ── Folder context-menu commands (right-click on a folder in the tree) ──

    /// <summary>All tracks under a folder node (direct + descendants), in tree order.</summary>
    private static List<Track> CollectTracks(FolderNode? node)
    {
        var sink = new List<Track>();
        if (node != null)
            Collect(node, sink);
        return sink;
    }

    [RelayCommand]
    private void PlayNode(FolderNode node)
    {
        var tracks = CollectTracks(node);
        if (tracks.Count == 0) return;
        _player.ReplaceQueueAndPlay(tracks, 0);
    }

    [RelayCommand]
    private void ShuffleNode(FolderNode node)
    {
        var tracks = CollectTracks(node);
        if (tracks.Count == 0) return;
        var shuffled = Helpers.ShuffleHelper.WeightedShuffle(tracks);
        _player.ReplaceQueueAndPlay(shuffled, 0);
    }

    [RelayCommand]
    private void PlayNodeNext(FolderNode node)
    {
        var tracks = CollectTracks(node);
        // Add in reverse order so they play in folder order when inserted up front.
        for (int i = tracks.Count - 1; i >= 0; i--)
            _player.AddNext(tracks[i]);
    }

    [RelayCommand]
    private void AddNodeToQueue(FolderNode node)
    {
        var tracks = CollectTracks(node);
        if (tracks.Count == 0) return;
        _player.AddRangeToQueue(tracks);
    }

    [RelayCommand]
    private async Task AddNodeToNewPlaylist(FolderNode node)
    {
        var tracks = CollectTracks(node);
        if (tracks.Count == 0) return;
        await _sidebar.CreatePlaylistWithTracksAsync(tracks);
    }

    [RelayCommand]
    private void ShowNodeInExplorer(FolderNode node)
    {
        if (node == null || !Directory.Exists(node.FullPath)) return;
        PlatformHelper.OpenFolder(node.FullPath);
    }

    // ── Track context-menu commands (same menu as the Songs view) ──

    [RelayCommand]
    private void PlayNext(Track track) => _player.AddNext(track);

    [RelayCommand]
    private void AddToQueue(Track track) => _player.AddToQueue(track);

    [RelayCommand]
    private void StartRadio(Track track) => _player.StartRadioCommand.Execute(track);

    [RelayCommand]
    private void SnoozeForMonth(Track track) => _player.SnoozeForMonthCommand.Execute(track);

    [RelayCommand]
    private async Task AddToNewPlaylist(Track track)
    {
        if (track == null) return;
        await _sidebar.CreatePlaylistWithTracksAsync(new List<Track> { track });
    }

    [RelayCommand]
    private async Task OpenMetadata(Track track)
    {
        if (track == null) return;
        await MetadataHelper.OpenMetadataWindow(track);
    }

    [RelayCommand]
    private async Task ConvertTracks(Track track)
    {
        if (track == null) return;
        await MetadataHelper.OpenAudioConverterDialog(new List<Track> { track });
    }

    [RelayCommand]
    private async Task ScanReplayGain(Track track)
    {
        if (track == null) return;
        await MetadataHelper.OpenReplayGainScannerDialog(new List<Track> { track });
    }

    [RelayCommand]
    private async Task ToggleFavorite(Track track)
    {
        if (track == null) return;
        track.IsFavorite = !track.IsFavorite;
        await _library.SaveAsync();
        _library.NotifyFavoritesChanged();
    }

    [RelayCommand]
    private void ShowInExplorer(Track track)
    {
        if (track == null || !File.Exists(track.FilePath)) return;
        PlatformHelper.ShowInFileManager(track.FilePath);
    }

    [RelayCommand]
    private async Task RemoveFromLibrary(Track track)
    {
        if (track == null) return;
        await LibraryRemovalHelper.RemoveWithPromptAsync(_library, new List<Track> { track });
    }

    private Action<Track>? _searchLyricsAction;
    public void SetSearchLyricsAction(Action<Track> action) => _searchLyricsAction = action;

    [RelayCommand]
    private void SearchLyrics(Track track) => _searchLyricsAction?.Invoke(track);

    /// <summary>Fires when a track title is clicked — MainWindowViewModel opens the album detail page.</summary>
    public event EventHandler<Track>? ViewAlbumRequested;

    [RelayCommand]
    private void ViewAlbum(Track track)
    {
        if (track == null) return;
        ViewAlbumRequested?.Invoke(this, track);
    }

    private Action<string>? _viewArtistAction;
    public void SetViewArtistAction(Action<string> action) => _viewArtistAction = action;

    [RelayCommand]
    private void ViewArtist(string artistName)
    {
        if (!string.IsNullOrWhiteSpace(artistName))
            _viewArtistAction?.Invoke(artistName);
    }

    public void Dispose()
    {
        if (_libraryUpdatedHandler != null)
        {
            _library.LibraryUpdated -= _libraryUpdatedHandler;
            _libraryUpdatedHandler = null;
        }
    }
}
