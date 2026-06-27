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

    private EventHandler? _libraryUpdatedHandler;
    private bool _isDirty = true;
    private string _currentFilter = string.Empty;

    /// <summary>Root nodes of the folder forest (one per configured music root).</summary>
    public ObservableCollection<FolderNode> RootNodes { get; } = new();

    /// <summary>Tracks to display in the right-hand pane (direct + descendant tracks of SelectedNode).</summary>
    public BulkObservableCollection<Track> SelectedFolderTracks { get; } = new();

    [ObservableProperty] private FolderNode? _selectedNode;

    /// <summary>Id of the track currently loaded in the player (drives the now-playing row highlight).</summary>
    [ObservableProperty] private Guid? _currentPlayingTrackId;
    private readonly System.ComponentModel.PropertyChangedEventHandler _playerPropertyChangedHandler;

    /// <summary>Fires when the user clicks "Manage media folders…" — handled by MainWindowViewModel to switch views.</summary>
    public event EventHandler? NavigateToSettingsRequested;

    public LibraryFoldersViewModel(ILibraryService library, PlayerViewModel player, IPersistenceService persistence)
    {
        _library = library;
        _player = player;
        _persistence = persistence;

        CurrentPlayingTrackId = _player.CurrentTrack?.Id;
        _playerPropertyChangedHandler = (_, e) =>
        {
            if (e.PropertyName == nameof(PlayerViewModel.CurrentTrack))
                CurrentPlayingTrackId = _player.CurrentTrack?.Id;
        };
        _player.PropertyChanged += _playerPropertyChangedHandler;

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

        var forest = await Task.Run(() => FolderTreeBuilder.Build(tracks, roots));

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

        SelectedFolderTracks.ReplaceAll(sink);
    }

    private static void Collect(FolderNode node, List<Track> sink)
    {
        sink.AddRange(node.DirectTracks);
        foreach (var child in node.Children)
            Collect(child, sink);
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

    public void Dispose()
    {
        _player.PropertyChanged -= _playerPropertyChangedHandler;

        if (_libraryUpdatedHandler != null)
        {
            _library.LibraryUpdated -= _libraryUpdatedHandler;
            _libraryUpdatedHandler = null;
        }
    }
}
