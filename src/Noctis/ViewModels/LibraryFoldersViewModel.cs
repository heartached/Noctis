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
public partial class LibraryFoldersViewModel : ViewModelBase, IDisposable
{
    private readonly ILibraryService _library;
    private readonly PlayerViewModel _player;
    private readonly IPersistenceService _persistence;

    private EventHandler? _libraryUpdatedHandler;
    private bool _isDirty = true;

    /// <summary>Root nodes of the folder forest (one per configured music root).</summary>
    public ObservableCollection<FolderNode> RootNodes { get; } = new();

    /// <summary>Tracks to display in the right-hand pane (direct + descendant tracks of SelectedNode).</summary>
    public BulkObservableCollection<Track> SelectedFolderTracks { get; } = new();

    [ObservableProperty] private FolderNode? _selectedNode;

    public LibraryFoldersViewModel(ILibraryService library, PlayerViewModel player, IPersistenceService persistence)
    {
        _library = library;
        _player = player;
        _persistence = persistence;

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
        await RefreshAsync();
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

    partial void OnSelectedNodeChanged(FolderNode? value)
    {
        if (value == null)
        {
            SelectedFolderTracks.ReplaceAll(Array.Empty<Track>());
            return;
        }

        var sink = new List<Track>();
        Collect(value, sink);
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
        var shuffled = SelectedFolderTracks.OrderBy(_ => Random.Shared.Next()).ToList();
        _player.ReplaceQueueAndPlay(shuffled, 0);
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
        if (_libraryUpdatedHandler != null)
        {
            _library.LibraryUpdated -= _libraryUpdatedHandler;
            _libraryUpdatedHandler = null;
        }
    }
}
