using System.Text.RegularExpressions;
using Noctis.Models;

namespace Noctis.Helpers;

/// <summary>
/// Folder-derived metadata for files whose tags are missing (GitHub/Discord
/// reports: iTunes WAV rips carry no tags at all, so whole collections fell
/// into "Unknown Artist" and the shared Unknown-Album bucket). The layout
/// <c>&lt;root&gt;/&lt;Artist&gt;/&lt;Album&gt;/NN Title.ext</c> carries the
/// identity instead. Only ever fills placeholders — a real tag always wins.
/// </summary>
public static partial class FolderMetadata
{
    [GeneratedRegex(@"^(?:(\d{1,2})-)?(\d{1,2})[ .\-_]+(.+)$")]
    private static partial Regex TrackPrefixRegex();

    [GeneratedRegex(@"^(cd|disc|disk)\s*\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex DiscFolderRegex();

    /// <summary>
    /// Derives (artist, album) from the file's folder structure: album = parent
    /// folder, artist = grandparent. Disc subfolders ("CD1", "Disc 2") are
    /// skipped. A configured music root or the volume root never becomes a
    /// credit, so a file sitting one level deep yields an album only, and a
    /// file directly in a root yields nothing.
    /// </summary>
    public static (string? Artist, string? Album) InferArtistAlbum(string filePath, IReadOnlyList<string> musicRoots)
    {
        var dir = SafeParent(filePath);
        if (dir == null) return (null, null);

        // Skip disc subfolders so multi-disc rips group as one album.
        if (DiscFolderRegex().IsMatch(FolderName(dir)))
        {
            dir = SafeParent(dir);
            if (dir == null) return (null, null);
        }

        if (IsRootLike(dir, musicRoots)) return (null, null);
        var album = FolderName(dir);
        if (string.IsNullOrWhiteSpace(album)) return (null, null);

        var artistDir = SafeParent(dir);
        if (artistDir == null || IsRootLike(artistDir, musicRoots))
            return (null, album);
        var artist = FolderName(artistDir);
        return (string.IsNullOrWhiteSpace(artist) ? null : artist, album);
    }

    /// <summary>
    /// Parses a "01 Title" / "12. Title" / "1-01 Title" filename prefix into
    /// (disc, track, clean title). Returns (0, 0, input) when there is no
    /// usable prefix — 3+ digit numbers (years) and digits-only names don't count.
    /// </summary>
    public static (int Disc, int Track, string Title) ParseTrackFilename(string title)
    {
        var m = TrackPrefixRegex().Match(title);
        if (!m.Success) return (0, 0, title);

        var disc = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0;
        var track = int.Parse(m.Groups[2].Value);
        var clean = m.Groups[3].Value.Trim();
        if (track == 0 || clean.Length == 0) return (0, 0, title);
        return (disc, track, clean);
    }

    /// <summary>
    /// Fills a track's placeholder fields from its path: artist/album from the
    /// folder structure, track number (and title, when the title is just the
    /// filename) from the "NN " prefix, and re-keys AlbumId when the identity
    /// changed. Returns true when anything was applied. Used by the scan for
    /// new files and by the load-time backfill for already-imported ones.
    /// </summary>
    public static bool TryApplyToTrack(Track track, IReadOnlyList<string> musicRoots)
    {
        // Everything below reads the path's shape — parent folders, a "NN " filename prefix —
        // so it is meaningful only for a real filesystem path. On Android every track is a
        // content:// document URI, whose percent-encoded segments would be read as
        // <Artist>/<Album> and written back as tags ("primary%3AMusic%2FTones" / "document"),
        // re-keying AlbumId into a phantom album as well. The scan already skips this for
        // stream-backed entries (MetadataService.BuildTrack, localPath == null); guarding here
        // covers the load-time backfill in LibraryService, which has no localPath to test.
        if (!track.HasFilesystemPath) return false;

        var changed = false;
        var artistMissing = IsPlaceholderArtist(track.Artist);
        var albumMissing = !Track.IsRealAlbumName(track.Album);

        if (artistMissing || albumMissing)
        {
            var (artist, album) = InferArtistAlbum(track.FilePath, musicRoots);
            if (artistMissing && artist != null)
            {
                track.Artist = artist;
                if (IsPlaceholderArtist(track.AlbumArtist))
                    track.AlbumArtist = artist;
                changed = true;
            }
            if (albumMissing && album != null)
            {
                track.Album = album;
                changed = true;
            }
            if (changed)
                track.AlbumId = Track.ComputeAlbumId(
                    IsPlaceholderArtist(track.AlbumArtist) ? "Unknown Artist" : track.AlbumArtist,
                    Track.IsRealAlbumName(track.Album) ? track.Album : "Unknown Album");
        }

        var fileName = Path.GetFileNameWithoutExtension(track.FilePath);
        var (disc, number, cleanTitle) = ParseTrackFilename(fileName);
        if (number > 0 && track.TrackNumber <= 0)
        {
            track.TrackNumber = number;
            // Only rewrite the title when it is just the filename fallback —
            // a real title tag is never touched.
            if (string.Equals(track.Title, fileName, StringComparison.Ordinal))
                track.Title = cleanTitle;
            if (disc > 0 && track.DiscNumber <= 1)
                track.DiscNumber = disc;
            changed = true;
        }

        return changed;
    }

    private static bool IsPlaceholderArtist(string? artist) =>
        string.IsNullOrWhiteSpace(artist) ||
        artist.Trim().Equals("Unknown Artist", StringComparison.OrdinalIgnoreCase);

    private static string? SafeParent(string path)
    {
        try
        {
            var parent = Path.GetDirectoryName(path);
            return string.IsNullOrEmpty(parent) ? null : parent;
        }
        catch
        {
            return null;
        }
    }

    private static string FolderName(string dir) => Path.GetFileName(dir) ?? string.Empty;

    /// <summary>A configured music root, one of the user's own folders, or a directory with
    /// no parent (volume root).</summary>
    private static bool IsRootLike(string dir, IReadOnlyList<string> musicRoots)
    {
        var normalized = TrimSeparators(dir);
        if (string.IsNullOrEmpty(FolderName(normalized)))
            return true; // volume root — GetFileName of "C:\" or "/" is empty

        foreach (var root in musicRoots.Concat(UserFolders.Value))
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            if (string.Equals(normalized, TrimSeparators(root), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// GitHub #108: files added where they are often sit in Downloads or on the Desktop.
    /// The profile and its standard folders hold files; they are no artist or album
    /// (a file in Downloads was credited as album "Downloads" by the account name).
    /// </summary>
    private static readonly Lazy<string[]> UserFolders = new(() =>
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var folders = new[]
        {
            Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyVideos,
            Environment.SpecialFolder.MyPictures,
        }.Select(Environment.GetFolderPath).Append(profile);
        if (!string.IsNullOrEmpty(profile))
            folders = folders.Append(Path.Combine(profile, "Downloads"));
        return folders.Where(f => !string.IsNullOrWhiteSpace(f)).ToArray();
    });

    private static string TrimSeparators(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
