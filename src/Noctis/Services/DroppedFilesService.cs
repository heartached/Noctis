using System.IO;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Services;

/// <summary>
/// GitHub #108: files dropped onto Noctis with "Import dropped files" off go into the
/// library, a new playlist or an existing one where they are — nothing is moved or copied.
/// The library marks files outside the music folders (Track.AddedIndividually), so scans
/// keep them. Files already in the library reuse their track: no duplicates, and the
/// playlist gets the existing ids.
/// </summary>
public sealed class DroppedFilesService
{
    private readonly ILibraryService _library;
    private readonly SidebarViewModel _sidebar;

    public DroppedFilesService(ILibraryService library, SidebarViewModel sidebar)
    {
        _library = library;
        _sidebar = sidebar;
    }

    /// <summary>What a drop did: its tracks in drop order and how many of them were new to
    /// the target (the library, or the playlist).</summary>
    public sealed record Result(IReadOnlyList<Track> Tracks, int Added, Playlist? Playlist = null)
    {
        public static readonly Result Empty = new(Array.Empty<Track>(), 0);
    }

    /// <summary>Adds the dropped files (folders expanded) to the library where they are.
    /// <paramref name="progress"/> gets (files read so far, files in the drop).</summary>
    public async Task<Result> AddToLibraryAsync(IReadOnlyList<string> droppedPaths,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        var files = await Task.Run(() => ExpandAudioFiles(droppedPaths), ct);
        return await AddFilesAsync(files, progress, ct);
    }

    /// <summary>AIMP-style: a new playlist named after the dropped files' folder, holding
    /// them in drop order.</summary>
    public async Task<Result> CreatePlaylistAsync(IReadOnlyList<string> droppedPaths, string fallbackName,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        var added = await AddToLibraryAsync(droppedPaths, progress, ct);
        if (added.Tracks.Count == 0) return Result.Empty;
        var playlist = await _sidebar.CreatePlaylistFromTracksAsync(PlaylistNameFor(droppedPaths, fallbackName), added.Tracks);
        return new Result(added.Tracks, added.Tracks.Count, playlist);
    }

    /// <summary>Appends the dropped files to an existing manual playlist (songs it already
    /// holds are not added twice).</summary>
    public async Task<Result> AddToPlaylistAsync(Guid playlistId, IReadOnlyList<string> droppedPaths,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        var added = await AddToLibraryAsync(droppedPaths, progress, ct);
        return await AddTracksToPlaylistAsync(playlistId, added.Tracks);
    }

    /// <summary>Appends library tracks to an existing manual playlist; Added counts the ones
    /// it did not hold yet.</summary>
    public async Task<Result> AddTracksToPlaylistAsync(Guid playlistId, IReadOnlyList<Track> tracks)
    {
        var playlist = _sidebar.Playlists.FirstOrDefault(p => p.Id == playlistId);
        if (playlist == null || playlist.IsSmartPlaylist || tracks.Count == 0)
            return new Result(tracks, 0, playlist);
        var held = new HashSet<Guid>(playlist.TrackIds);
        var fresh = tracks.Count(t => held.Add(t.Id));
        await _sidebar.AddTracksToPlaylist(playlistId, tracks);
        return new Result(tracks, fresh, playlist);
    }

    private async Task<Result> AddFilesAsync(IReadOnlyList<string> files,
        IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        if (files.Count == 0) return Result.Empty;
        var alreadyIn = new HashSet<Guid>(files
            .Select(LibraryService.TrackIdForPath)
            .Where(id => _library.GetTrackById(id) != null));

        var perFile = progress == null ? null : new Relay(n => progress.Report((Math.Min(n, files.Count), files.Count)));
        await _library.ImportFilesAsync(files, ct, perFile);

        var tracks = TracksFor(files);
        return new Result(tracks, tracks.Count(t => !alreadyIn.Contains(t.Id)));
    }

    /// <summary>The library tracks for <paramref name="files"/>, in order, each once.</summary>
    private List<Track> TracksFor(IEnumerable<string> files)
    {
        var tracks = new List<Track>();
        var seen = new HashSet<Guid>();
        foreach (var file in files)
        {
            if (_library.GetTrackById(LibraryService.TrackIdForPath(file)) is { } track && seen.Add(track.Id))
                tracks.Add(track);
        }
        return tracks;
    }

    /// <summary>Dropped files and folders → the playable files in drop order, folders walked
    /// recursively and sorted by path in file-manager (numeric-aware) order so disc/track
    /// prefixes give the play order.</summary>
    public static List<string> ExpandAudioFiles(IReadOnlyList<string> paths)
    {
        var files = new List<string>();
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (Directory.Exists(raw))
            {
                var perFolder = new List<string>();
                try
                {
                    foreach (var file in Directory.EnumerateFiles(raw, "*.*", SearchOption.AllDirectories))
                        if (MetadataService.SupportedExtensions.Contains(Path.GetExtension(file)))
                            perFolder.Add(file);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DroppedFiles] Failed to enumerate dropped folder {raw}: {ex.Message}");
                    continue;
                }
                perFolder.Sort(NaturalStringComparer.Instance);
                files.AddRange(perFolder);
            }
            else if (File.Exists(raw) && MetadataService.SupportedExtensions.Contains(Path.GetExtension(raw)))
                files.Add(raw);
        }
        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The name AIMP gives a playlist made from a drop: the folder the dropped files sit in,
    /// or a dropped folder's own name. Items from several folders take their closest common
    /// folder. A drive root or items on different drives leave nothing to go on:
    /// <paramref name="fallback"/>.
    /// </summary>
    public static string PlaylistNameFor(IReadOnlyList<string> droppedPaths, string fallback)
    {
        string? common = null;
        foreach (var raw in droppedPaths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string folder;
            try
            {
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
                folder = Directory.Exists(full) ? full : Path.GetDirectoryName(full) ?? string.Empty;
            }
            catch
            {
                continue;
            }
            if (folder.Length == 0) continue;
            common = common == null ? folder : CommonFolder(common, folder);
            if (common == null) break;
        }

        var name = common == null ? null : Path.GetFileName(Path.TrimEndingDirectorySeparator(common));
        return string.IsNullOrWhiteSpace(name) ? fallback : name;
    }

    /// <summary>The deepest folder holding both, or null when they share none (other drive).</summary>
    private static string? CommonFolder(string a, string b)
    {
        for (var dir = a; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            var root = Path.TrimEndingDirectorySeparator(dir);
            if (LibraryService.IsUnderRoot(b, root) || string.Equals(b, dir, PathComparison.Comparison))
                return dir;
        }
        return null;
    }

    /// <summary>Forwards on the reporting thread (Progress&lt;T&gt; would post to whatever context
    /// the service happens to run on; the caller's own Progress does the UI marshalling).</summary>
    private sealed class Relay(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
