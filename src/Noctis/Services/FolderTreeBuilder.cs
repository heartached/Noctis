using System.Collections.Generic;
using System.IO;
using System.Linq;
using Noctis.Helpers;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Builds a folder-hierarchy forest (one tree per configured music root) from a flat track list.
/// Pure function — no I/O, no state. Tracks whose FilePath lies outside every root are ignored.
/// </summary>
public static class FolderTreeBuilder
{
    public static IReadOnlyList<FolderNode> Build(
        IReadOnlyList<Track> tracks,
        IReadOnlyList<string> roots)
    {
        var normalizedRoots = roots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rootNodes = normalizedRoots.ToDictionary(
            r => r,
            r => new FolderNode
            {
                FullPath = r,
                DisplayName = Path.GetFileName(r) is { Length: > 0 } name ? name : r,
                IsRoot = true,
            },
            StringComparer.OrdinalIgnoreCase);

        var childIndex = new Dictionary<string, Dictionary<string, FolderNode>>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in rootNodes.Values)
            childIndex[root.FullPath] = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var track in tracks)
        {
            if (string.IsNullOrWhiteSpace(track.FilePath)) continue;
            var trackDir = NormalizePath(Path.GetDirectoryName(track.FilePath) ?? string.Empty);
            if (string.IsNullOrEmpty(trackDir)) continue;

            var root = normalizedRoots.FirstOrDefault(r =>
                trackDir.Equals(r, StringComparison.OrdinalIgnoreCase) ||
                trackDir.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            if (root == null) continue;

            var node = EnsureNode(rootNodes[root], trackDir, root, childIndex);
            node.DirectTracks.Add(track);
        }

        foreach (var root in rootNodes.Values)
            Finalize(root);

        return rootNodes.Values
            .OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static FolderNode EnsureNode(
        FolderNode rootNode,
        string targetDir,
        string rootPath,
        Dictionary<string, Dictionary<string, FolderNode>> childIndex)
    {
        if (targetDir.Equals(rootPath, StringComparison.OrdinalIgnoreCase))
            return rootNode;

        var relative = targetDir.Substring(rootPath.Length)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var segments = relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        var current = rootNode;
        var currentPath = rootPath;
        foreach (var segment in segments)
        {
            var nextPath = Path.Combine(currentPath, segment);
            var map = childIndex[currentPath];
            if (!map.TryGetValue(segment, out var child))
            {
                child = new FolderNode
                {
                    FullPath = nextPath,
                    DisplayName = segment,
                    IsRoot = false,
                };
                map[segment] = child;
                current.Children.Add(child);
                childIndex[nextPath] = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase);
            }
            current = child;
            currentPath = nextPath;
        }
        return current;
    }

    private static int Finalize(FolderNode node)
    {
        var total = node.DirectTracks.Count;

        // Deterministic, Explorer-like track order: disc/track tags when present
        // (untagged files carry TrackNumber 0), natural filename order for ties —
        // without this, untagged rips surface in library order, which is
        // Artist/Album/Disc/Track with the tie broken by parallel-scan insertion
        // order, i.e. arbitrary. Properly tagged folders stay in disc/track order.
        var sortedTracks = node.DirectTracks
            .OrderBy(t => t.DiscNumber <= 0 ? 1 : t.DiscNumber)
            .ThenBy(t => t.TrackNumber)
            .ThenBy(t => Path.GetFileName(t.FilePath), NaturalStringComparer.Instance)
            .ThenBy(t => t.FilePath, StringComparer.Ordinal)
            .ToList();
        node.DirectTracks.Clear();
        node.DirectTracks.AddRange(sortedTracks);

        // Natural order for subfolders too ("Disc 2" before "Disc 10"), so numbered
        // volume folders play and display in Explorer order when a parent is selected.
        var sortedChildren = node.Children
            .OrderBy(c => c.DisplayName, NaturalStringComparer.Instance)
            .ToList();
        node.Children.Clear();
        foreach (var child in sortedChildren)
        {
            total += Finalize(child);
            node.Children.Add(child);
        }
        node.TotalTrackCount = total;
        return total;
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path;
        }
    }
}
