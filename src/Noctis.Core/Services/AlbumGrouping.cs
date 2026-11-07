using System.Text;
using Noctis.Helpers;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// GitHub #178: songs of one album split into one album per singer. A file without an
/// album-artist tag gets its first performer as album artist (<see cref="Track.ResolveAlbumArtist"/>),
/// and the album key is album artist + album name (<see cref="Track.ComputeAlbumId"/>), so a
/// soundtrack with a different singer on every song became as many one-song albums.
///
/// The rule: songs whose album artist Noctis filled in (<see cref="Track.AlbumArtistFromTag"/>
/// false, not compilation-flagged) that share an album name (case- and spacing-insensitive)
/// in the same folder (a "CD1"/"Disc 2" subfolder counts as its parent) are one album. Its
/// album artist is
///   1. the album artist of the folder's tagged songs of that album, when they agree on one
///      (a partly tagged album: the untagged songs join it);
///   2. otherwise the first credited artist every song shares (the composer of a soundtrack
///      credited on every song: "Sushin Shyam, Anwar Ali" and "Sushin Shyam" → Sushin Shyam);
///   3. otherwise "Various Artists", as for compilations.
/// A real album-artist tag is never overridden, and the same album name in two folders stays
/// two albums ("Greatest Hits" by two artists). An album name Noctis took from the folder
/// (<see cref="Track.AlbumFromFolder"/>, a file without an album tag) is no album evidence: a
/// folder of loose singles is not one album (GitHub #193).
///
/// The rule can be switched off (Settings › Library, GitHub #193): <see cref="Restore"/> puts
/// every re-keyed song back on the album artist and album name it had before
/// (<see cref="Track.UngroupedAlbumArtist"/>, kept by <see cref="Apply"/>).
///
/// Stable by construction: a folder's untagged songs that already share one album key are left
/// alone, so a pass over an unchanged library changes nothing; a new or re-read song (which
/// arrives keyed by its performer) makes the folder's songs disagree again and the whole set is
/// re-keyed by the same rule, so it joins the existing album.
/// </summary>
public static class AlbumGrouping
{
    /// <summary>One song re-keyed from album <paramref name="From"/> to <paramref name="To"/>.</summary>
    public readonly record struct Move(Guid TrackId, Guid From, Guid To);

    /// <summary>A song the rule may re-key: album artist filled in by Noctis, a real album name
    /// from the file's own tag (not its folder's name, GitHub #193), not a compilation (those are
    /// "Various Artists" already).</summary>
    public static bool IsCandidate(Track t) =>
        t.AlbumArtistFromTag == false && !t.IsCompilation && !t.AlbumFromFolder && Track.IsRealAlbumName(t.Album);

    private static bool IsTaggedPeer(Track t) =>
        t.AlbumArtistFromTag == true && !t.IsCompilation && !t.AlbumFromFolder && Track.IsRealAlbumName(t.Album)
        && !string.IsNullOrWhiteSpace(t.AlbumArtist);

    /// <summary>Album name as the rule compares it: trimmed, inner runs of spaces collapsed.
    /// Compared case-insensitively (the dictionaries below). Returns the input when it is
    /// already in that form, so a library pass allocates nothing for ordinary names.</summary>
    public static string AlbumKey(string album)
    {
        var s = album.AsSpan();
        var clean = s.Length > 0 && !char.IsWhiteSpace(s[0]) && !char.IsWhiteSpace(s[^1]);
        for (var i = 1; clean && i < s.Length; i++)
            if (char.IsWhiteSpace(s[i]) && (s[i] != ' ' || char.IsWhiteSpace(s[i - 1])))
                clean = false;
        if (clean) return album;

        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var c in s.Trim())
        {
            if (char.IsWhiteSpace(c)) { space = true; continue; }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The folder a song's file sits in, as a comparison key (separators unified, lower case),
    /// with a disc subfolder ("CD1", "Disc 2") folded into its parent. Android document URIs
    /// ("content://…/document/primary%3AMusic%2FAlbum%2Fa.mp3") use the decoded document path.
    /// Null when the path has no folder: a server stream, a MediaStore id
    /// ("content://media/external/audio/media/42"), a file at a root.
    /// </summary>
    public static string? FolderKey(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string p;
        if (Track.IsFilesystemPath(path))
        {
            p = path.Replace('\\', '/');
        }
        else if (path.StartsWith("content://", StringComparison.OrdinalIgnoreCase))
        {
            var doc = path.LastIndexOf("/document/", StringComparison.Ordinal);
            if (doc < 0) return null;
            try { p = "content:" + Uri.UnescapeDataString(path[(doc + "/document/".Length)..]).Replace('\\', '/'); }
            catch (UriFormatException) { return null; }
        }
        else
        {
            return null;
        }

        var cut = p.LastIndexOf('/');
        if (cut <= 0) return null;
        var dir = p[..cut].TrimEnd('/');
        var nameAt = dir.LastIndexOf('/');
        if (nameAt > 0 && FolderMetadata.IsDiscFolderName(dir[(nameAt + 1)..]))
            dir = dir[..nameAt];
        return dir.Length == 0 ? null : dir.ToLowerInvariant();
    }

    /// <summary>
    /// Applies the rule to <paramref name="tracks"/> in place (AlbumArtist, AlbumId and, for a
    /// spacing/case variant, Album) and returns the songs it re-keyed, in album order, so the
    /// caller can carry album-keyed data (cached covers, pins) to the new key.
    /// </summary>
    public static List<Move> Apply(IReadOnlyList<Track> tracks)
    {
        var moves = new List<Move>();

        Dictionary<string, List<Track>>? byName = null;
        foreach (var t in tracks)
        {
            if (!IsCandidate(t)) continue;
            byName ??= new Dictionary<string, List<Track>>(StringComparer.OrdinalIgnoreCase);
            var key = AlbumKey(t.Album);
            if (!byName.TryGetValue(key, out var list)) byName[key] = list = new List<Track>();
            list.Add(t);
        }
        if (byName == null) return moves;

        // Tagged songs of the same album names: a partly tagged album's untagged songs join it.
        Dictionary<string, List<Track>>? taggedByName = null;
        foreach (var t in tracks)
        {
            if (!IsTaggedPeer(t)) continue;
            var key = AlbumKey(t.Album);
            if (!byName.ContainsKey(key)) continue;
            taggedByName ??= new Dictionary<string, List<Track>>(StringComparer.OrdinalIgnoreCase);
            if (!taggedByName.TryGetValue(key, out var list)) taggedByName[key] = list = new List<Track>();
            list.Add(t);
        }

        foreach (var (name, candidates) in byName)
        {
            List<Track>? tagged = null;
            taggedByName?.TryGetValue(name, out tagged);
            if (tagged == null && (candidates.Count < 2 || AllSameAlbum(candidates))) continue;

            var byFolder = new Dictionary<string, (List<Track> Candidates, List<Track> Tagged)>(StringComparer.Ordinal);
            foreach (var c in candidates)
            {
                var folder = FolderKey(c.FilePath);
                if (folder == null) continue;
                if (!byFolder.TryGetValue(folder, out var entry))
                    byFolder[folder] = entry = (new List<Track>(), new List<Track>());
                entry.Candidates.Add(c);
            }
            if (tagged != null)
                foreach (var t in tagged)
                    if (FolderKey(t.FilePath) is { } folder && byFolder.TryGetValue(folder, out var entry))
                        entry.Tagged.Add(t);

            foreach (var (_, entry) in byFolder)
                GroupFolder(entry.Candidates, entry.Tagged, moves);
        }

        return moves;
    }

    /// <summary>
    /// GitHub #193: puts the songs the rule re-keyed back on their own album artist and album
    /// name (<see cref="Track.UngroupedAlbumArtist"/>) once the rule no longer applies to them:
    /// the grouping is switched off (<paramref name="groupingEnabled"/> false), or the song is no
    /// candidate any more (its album name turned out to be its folder's). A song whose album
    /// artist is a real tag by now, or whose album was renamed since, keeps its album and drops
    /// the old values. Returns the songs moved, for the same carrying as <see cref="Apply"/>.
    /// </summary>
    public static List<Move> Restore(IReadOnlyList<Track> tracks, bool groupingEnabled)
    {
        var moves = new List<Move>();
        foreach (var t in tracks)
        {
            if (t.UngroupedAlbumArtist is not { } artist) continue;
            var album = t.UngroupedAlbum ?? t.Album;
            if (t.AlbumArtistFromTag == true || string.IsNullOrWhiteSpace(artist)
                || !string.Equals(AlbumKey(t.Album), AlbumKey(album), StringComparison.OrdinalIgnoreCase))
            {
                ForgetUngrouped(t);
                continue;
            }
            if (groupingEnabled && IsCandidate(t)) continue;

            var id = Track.ComputeAlbumId(artist, album);
            if (id != t.AlbumId) moves.Add(new Move(t.Id, t.AlbumId, id));
            t.AlbumArtist = artist;
            t.Album = album;
            t.AlbumId = id;
            ForgetUngrouped(t);
        }
        return moves;
    }

    private static void ForgetUngrouped(Track t)
    {
        t.UngroupedAlbumArtist = null;
        t.UngroupedAlbum = null;
    }

    /// <summary>
    /// GitHub #190: the songs the rule has re-keyed into a grouped album that their files do not
    /// name yet: candidates whose own album key (<see cref="Track.UngroupedAlbumArtist"/>) differs
    /// from the grouped one. Writing the album artist to these files makes other players show
    /// the album as Noctis does.
    /// </summary>
    public static List<Track> RegroupedSongs(IReadOnlyList<Track> tracks)
    {
        var result = new List<Track>();
        foreach (var t in tracks)
            if (t.UngroupedAlbumArtist is { } artist && IsCandidate(t)
                && Track.ComputeAlbumId(artist, t.UngroupedAlbum ?? t.Album) != t.AlbumId)
                result.Add(t);
        return result;
    }

    /// <summary>
    /// GitHub #193, libraries grouped by 1.6.8: the rule re-keyed songs before it kept their own
    /// values (<see cref="Track.UngroupedAlbumArtist"/>) and before an album name taken from the
    /// folder was told apart (<see cref="Track.AlbumFromFolder"/>). Returns the songs that may
    /// have been re-keyed, so only their files are re-read: the untagged songs of one album name
    /// in one folder that share one album key although their credits differ, or although a
    /// credit does not start with the album artist ("Various Artists", a tagged song's album
    /// artist). A plain one-artist album is not among them.
    /// </summary>
    public static List<Track> UnrecordedRegroupedSongs(IReadOnlyList<Track> tracks)
    {
        var result = new List<Track>();
        Dictionary<(string Name, string Folder), List<Track>>? groups = null;
        foreach (var t in tracks)
        {
            if (t.UngroupedAlbumArtist != null || !IsCandidate(t)) continue;
            if (FolderKey(t.FilePath) is not { } folder) continue;
            groups ??= new Dictionary<(string, string), List<Track>>(NameFolderComparer.Instance);
            var key = (AlbumKey(t.Album), folder);
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<Track>();
            list.Add(t);
        }
        if (groups == null) return result;

        foreach (var list in groups.Values)
        {
            if (!AllSameAlbum(list)) continue;
            var creditsDiffer = list
                .Select(t => (t.Artist ?? string.Empty).Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Skip(1).Any();
            if (creditsDiffer || list.Any(t => !(t.Artist ?? string.Empty).TrimStart()
                    .StartsWith((t.AlbumArtist ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)))
                result.AddRange(list);
        }
        return result;
    }

    private static void GroupFolder(List<Track> candidates, List<Track> tagged, List<Move> moves)
    {
        candidates.Sort(AlbumOrder);
        string artist;
        string album;

        var taggedArtists = tagged
            .Select(t => t.AlbumArtist.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (taggedArtists.Count == 1)
        {
            tagged.Sort(AlbumOrder);
            artist = tagged[0].AlbumArtist.Trim();
            album = MostCommonSpelling(tagged);
        }
        else
        {
            if (candidates.Count < 2 || AllSameAlbum(candidates)) return;
            artist = SharedArtist(candidates) ?? Track.VariousArtists;
            album = MostCommonSpelling(candidates);
        }

        var target = Track.ComputeAlbumId(artist, album);
        foreach (var c in candidates)
        {
            if (c.AlbumId == target) continue;
            moves.Add(new Move(c.Id, c.AlbumId, target));
            // The file's own values, kept so switching the grouping off puts the song back
            // (GitHub #193). The first re-key's: a later one starts from a grouped value.
            if (c.UngroupedAlbumArtist == null)
            {
                c.UngroupedAlbumArtist = c.AlbumArtist;
                c.UngroupedAlbum = c.Album;
            }
            c.AlbumArtist = artist;
            c.Album = album;
            c.AlbumId = target;
        }
    }

    /// <summary>The first credited artist (in the first song's credit order) every song credits.</summary>
    internal static string? SharedArtist(IReadOnlyList<Track> ordered)
    {
        var first = ArtistCredit.Split(ordered[0].Artist);
        if (first.Length == 0) return null;
        var others = ordered.Skip(1)
            .Select(t => new HashSet<string>(ArtistCredit.Split(t.Artist), StringComparer.OrdinalIgnoreCase))
            .ToList();
        foreach (var name in first)
            if (!string.IsNullOrWhiteSpace(name) && others.All(set => set.Contains(name)))
                return name.Trim();
        return null;
    }

    private static string MostCommonSpelling(IEnumerable<Track> tracks) => tracks
        .GroupBy(t => t.Album.Trim(), StringComparer.Ordinal)
        .OrderByDescending(g => g.Count())
        .ThenBy(g => g.Key, StringComparer.Ordinal)
        .First().Key;

    private static bool AllSameAlbum(List<Track> tracks)
    {
        var id = tracks[0].AlbumId;
        for (var i = 1; i < tracks.Count; i++)
            if (tracks[i].AlbumId != id) return false;
        return true;
    }

    internal static int AlbumOrder(Track a, Track b)
    {
        var c = (a.DiscNumber <= 0 ? 1 : a.DiscNumber).CompareTo(b.DiscNumber <= 0 ? 1 : b.DiscNumber);
        if (c != 0) return c;
        c = (a.TrackNumber <= 0 ? int.MaxValue : a.TrackNumber).CompareTo(b.TrackNumber <= 0 ? int.MaxValue : b.TrackNumber);
        return c != 0 ? c : string.CompareOrdinal(a.FilePath, b.FilePath);
    }

    /// <summary>
    /// Defaults for "Group into One Album" over <paramref name="tracks"/>: the most common album
    /// name, and the album artist they all share, else the first credited artist every song
    /// credits, else "Various Artists" (the automatic rule's choices).
    /// </summary>
    public static (string Album, string AlbumArtist) SuggestGroupTarget(IReadOnlyList<Track> tracks)
    {
        var ordered = tracks.Where(t => t != null).ToList();
        if (ordered.Count == 0) return (string.Empty, string.Empty);
        ordered.Sort(AlbumOrder);
        var named = ordered.Where(t => Track.IsRealAlbumName(t.Album)).ToList();
        var album = named.Count > 0 ? MostCommonSpelling(named) : string.Empty;
        var artists = ordered
            .Select(t => t.AlbumArtist?.Trim() ?? string.Empty)
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var artist = artists.Count == 1 ? artists[0] : SharedArtist(ordered) ?? Track.VariousArtists;
        return (album, artist);
    }

    /// <summary>The albums named like <paramref name="album"/> (case- and spacing-insensitive),
    /// it first: what "Group into One Album" offers for a single album with nothing selected.</summary>
    public static List<Album> SameNameAlbums(IEnumerable<Album> albums, Album album)
    {
        var result = new List<Album> { album };
        if (!Track.IsRealAlbumName(album.Name)) return result;
        var key = AlbumKey(album.Name);
        foreach (var a in albums)
            if (a.Id != album.Id && Track.IsRealAlbumName(a.Name)
                && string.Equals(AlbumKey(a.Name), key, StringComparison.OrdinalIgnoreCase))
                result.Add(a);
        return result;
    }

    /// <summary>
    /// Songs scanned before <see cref="Track.AlbumArtistFromTag"/> existed (null) that sit in a
    /// split album: same album name and folder as other songs, but more than one album key.
    /// Only their files need re-reading to learn whether the album artist is a real tag; every
    /// other song keeps null and is never grouped until its next re-read.
    /// </summary>
    public static List<Track> UnresolvedInSplitAlbums(IReadOnlyList<Track> tracks)
    {
        var result = new List<Track>();
        Dictionary<(string Name, string Folder), List<Track>>? groups = null;
        HashSet<string>? namesWithUnknown = null;
        foreach (var t in tracks)
            if (t.AlbumArtistFromTag == null && !t.IsCompilation && Track.IsRealAlbumName(t.Album))
                (namesWithUnknown ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(AlbumKey(t.Album));
        if (namesWithUnknown == null) return result;

        foreach (var t in tracks)
        {
            if (t.IsCompilation || !Track.IsRealAlbumName(t.Album)) continue;
            var name = AlbumKey(t.Album);
            if (!namesWithUnknown.Contains(name)) continue;
            if (FolderKey(t.FilePath) is not { } folder) continue;
            groups ??= new Dictionary<(string, string), List<Track>>(NameFolderComparer.Instance);
            if (!groups.TryGetValue((name, folder), out var list)) groups[(name, folder)] = list = new List<Track>();
            list.Add(t);
        }
        if (groups == null) return result;

        foreach (var list in groups.Values)
            if (list.Count > 1 && !AllSameAlbum(list))
                result.AddRange(list.Where(t => t.AlbumArtistFromTag == null));
        return result;
    }

    private sealed class NameFolderComparer : IEqualityComparer<(string Name, string Folder)>
    {
        public static readonly NameFolderComparer Instance = new();
        public bool Equals((string Name, string Folder) x, (string Name, string Folder) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name) && string.Equals(x.Folder, y.Folder, StringComparison.Ordinal);
        public int GetHashCode((string Name, string Folder) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name), obj.Folder.GetHashCode());
    }
}
