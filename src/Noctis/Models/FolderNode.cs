using System.Collections.ObjectModel;

namespace Noctis.Models;

/// <summary>
/// A node in the folder browse tree. Computed on demand from library tracks;
/// not persisted. A node represents one directory under a configured music root.
/// </summary>
public sealed class FolderNode
{
    /// <summary>Absolute path of this folder on disk.</summary>
    public required string FullPath { get; init; }

    /// <summary>Leaf display name (e.g. "Rock"). For a root, the full root path.</summary>
    public required string DisplayName { get; init; }

    /// <summary>True if this node is one of the user's configured music roots.</summary>
    public bool IsRoot { get; init; }

    /// <summary>Child subfolders, alphabetically sorted.</summary>
    public ObservableCollection<FolderNode> Children { get; } = new();

    /// <summary>Tracks that live directly in this folder (not in subfolders).</summary>
    public List<Track> DirectTracks { get; } = new();

    /// <summary>Total track count including all descendants (computed at build time).</summary>
    public int TotalTrackCount { get; set; }
}
