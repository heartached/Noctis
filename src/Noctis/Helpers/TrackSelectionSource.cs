using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Noctis.Models;

namespace Noctis.Helpers;

/// <summary>
/// A page view whose list can hold a multi-selection (Ctrl+click / Ctrl+A). Window-level
/// shortcuts that act on "the selection" (GitHub #131: ReplayGain) read it through this
/// instead of reaching into each page. Albums and favorites expand to their tracks.
/// </summary>
public interface ITrackSelectionSource
{
    IReadOnlyList<Track> GetSelectedTracks();

    /// <summary>
    /// What a drag that starts on <paramref name="dragged"/> (its <paramref name="payload"/>: a
    /// song, album or favourite) carries: the whole selection, in list order, when the dragged
    /// item is part of a selection of several; null otherwise (the drag carries just that item).
    /// Read once, when the drag starts (DragFileBehavior).
    /// </summary>
    IReadOnlyList<Track>? GetDragSelection(Control dragged, object? payload) => null;
}

public static class TrackSelection
{
    /// <summary>Album selections expand to their tracks, album by album.</summary>
    public static List<Track> FromAlbums(IEnumerable<Album> albums)
    {
        var tracks = new List<Track>();
        foreach (var album in albums)
            if (album.Tracks is { } albumTracks) tracks.AddRange(albumTracks);
        return tracks;
    }

    /// <summary>
    /// What the ReplayGain shortcut scans, first non-empty wins: the queue panel's
    /// selection (when focus is in it), the current page's selection, then the playing
    /// track. Empty means there is nothing to scan and the key is left alone.
    /// Streams and disc tracks (an MRL such as <c>http://</c> or <c>cdda://</c>, not a file)
    /// are dropped from the playing-track fallback: the scanner reads files.
    /// </summary>
    public static IReadOnlyList<Track> ForReplayGain(
        IReadOnlyList<Track>? queueSelection,
        IReadOnlyList<Track>? pageSelection,
        Track? playing)
    {
        if (queueSelection is { Count: > 0 }) return queueSelection;
        if (pageSelection is { Count: > 0 }) return pageSelection;
        if (playing is not null && IsFile(playing.FilePath)) return new[] { playing };
        return Array.Empty<Track>();
    }

    /// <summary>
    /// The selection a drag of <paramref name="payload"/> carries (see
    /// <see cref="ITrackSelectionSource.GetDragSelection"/>), in the order of
    /// <paramref name="ordered"/> (the list as shown): null unless the payload is one of
    /// several selected items. Selected items the list no longer shows are left out.
    /// </summary>
    public static List<T>? DragSelection<T>(object? payload, HashSet<T> selected, IEnumerable? ordered) where T : class
    {
        if (payload is not T item || selected.Count < 2 || !selected.Contains(item)) return null;
        var result = new List<T>(selected.Count);
        if (ordered != null)
            foreach (var o in ordered)
                if (o is T t && selected.Contains(t))
                    result.Add(t);
        return result.Count > 0 ? result : null;
    }

    private static bool IsFile(string? path) =>
        !string.IsNullOrWhiteSpace(path) && !path.Contains("://", StringComparison.Ordinal);

    /// <summary>Queue-row indices (as <see cref="QueueRowSelection{T}.Snapshot"/> returns
    /// them) to tracks, skipping indices that no longer exist.</summary>
    public static List<Track> FromIndices(IReadOnlyList<Track> rows, IEnumerable<int> indices) =>
        indices.Where(i => i >= 0 && i < rows.Count).OrderBy(i => i).Select(i => rows[i]).ToList();
}
