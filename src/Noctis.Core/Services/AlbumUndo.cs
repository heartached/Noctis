using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Undo Grouping (Discord 11-27, Halil): "Group into One Album" (#178) and "Move to Album" (#239)
/// write the new album into the files, and nothing kept what the songs had before, so a grouping
/// could only be reversed by retagging every file by hand.
///
/// Each song a grouping changes keeps its album tags from before and from after
/// (<see cref="AlbumGroupUndo"/>, on <see cref="Track.GroupUndo"/>). A song grouped again keeps
/// its first "before", so Undo puts it back where it was before any grouping. The record applies
/// only while the song still has what the grouping left (<see cref="Applies(Track)"/>): an Edit
/// Info change or a file retagged elsewhere since wins, the record is dropped
/// (<see cref="Prune"/>, <see cref="Carry"/>) and Undo is not offered, so it never overwrites a
/// newer edit. Songs grouped by older builds have no record and offer no Undo.
/// </summary>
public static class AlbumUndo
{
    /// <summary>The song's album tags as a grouping changes them.</summary>
    public static AlbumTagValues Capture(Track t) => new(
        t.Album, t.AlbumArtist, t.TrackNumber, t.TrackCount, t.DiscNumber, t.DiscCount,
        t.IsCompilation, t.IsReleaseTypeOverridden, t.ReleaseType, t.Year, t.ReleaseDate);

    /// <summary>Whether the song has a record Undo Grouping can put back.</summary>
    public static bool Applies(Track t) => t.GroupUndo is { } undo && Applies(t, undo);

    /// <summary>
    /// Whether <paramref name="t"/> still has what the grouping left: the same album (as albums
    /// are matched, <see cref="Track.ComputeAlbumId"/>) and, for each other tag the grouping
    /// changed, the value it set. Tags the grouping did not change may have been edited since.
    /// </summary>
    public static bool Applies(Track t, AlbumGroupUndo undo)
    {
        var before = undo.Before;
        var after = undo.After;
        if (Track.ComputeAlbumId(t.AlbumArtist ?? string.Empty, t.Album ?? string.Empty)
            != Track.ComputeAlbumId(after.AlbumArtist, after.Album))
            return false;
        return Kept(before.TrackNumber, after.TrackNumber, t.TrackNumber)
               && Kept(before.TrackCount, after.TrackCount, t.TrackCount)
               && Kept(before.DiscNumber, after.DiscNumber, t.DiscNumber)
               && Kept(before.DiscCount, after.DiscCount, t.DiscCount)
               && Kept(before.IsCompilation, after.IsCompilation, t.IsCompilation)
               && Kept(before.IsReleaseTypeOverridden, after.IsReleaseTypeOverridden, t.IsReleaseTypeOverridden)
               // Without the override the type is worked out from the song, not a tag of its own.
               && (!after.IsReleaseTypeOverridden || Kept(before.ReleaseType, after.ReleaseType, t.ReleaseType))
               && Kept(before.Year, after.Year, t.Year);

        static bool Kept<T>(T was, T set, T now) =>
            EqualityComparer<T>.Default.Equals(was, set) || EqualityComparer<T>.Default.Equals(now, set);
    }

    /// <summary>The songs of <paramref name="tracks"/> Undo Grouping can put back.</summary>
    public static List<Track> UndoableSongs(IEnumerable<Track> tracks)
    {
        var result = new List<Track>();
        HashSet<Guid>? seen = null;
        foreach (var t in tracks)
        {
            if (t == null || !Applies(t)) continue;
            seen ??= new HashSet<Guid>();
            if (seen.Add(t.Id)) result.Add(t);
        }
        return result;
    }

    /// <summary>
    /// Before a grouping changes <paramref name="t"/>: the record to finish once it has. The
    /// song's own when that still applies (its first "before" is kept), else one of its tags now.
    /// <paramref name="identity"/> is the file's album identity tags when the grouping writes the
    /// target album's over them (Move to Album), else null.
    /// </summary>
    internal static AlbumGroupUndo Begin(Track t, AlbumIdentityTags? identity)
    {
        if (t.GroupUndo is { } kept && Applies(t, kept))
            return new AlbumGroupUndo
            {
                Before = kept.Before,
                After = kept.After,
                AlbumId = kept.AlbumId,
                AlbumArtistFromTag = kept.AlbumArtistFromTag,
                AlbumFromFolder = kept.AlbumFromFolder,
                UngroupedAlbumArtist = kept.UngroupedAlbumArtist,
                UngroupedAlbum = kept.UngroupedAlbum,
                // Group into One Album leaves these tags alone: the file's own are still the first.
                Identity = kept.Identity ?? identity,
            };
        return new AlbumGroupUndo
        {
            Before = Capture(t),
            AlbumId = t.AlbumId,
            AlbumArtistFromTag = t.AlbumArtistFromTag,
            AlbumFromFolder = t.AlbumFromFolder,
            UngroupedAlbumArtist = t.UngroupedAlbumArtist,
            UngroupedAlbum = t.UngroupedAlbum,
            Identity = identity,
        };
    }

    /// <summary>After the grouping changed <paramref name="t"/>: keeps the record, or none when
    /// the song has its own tags again (grouped back where it was).</summary>
    internal static void Finish(Track t, AlbumGroupUndo undo)
    {
        undo.After = Capture(t);
        // A file without its own album artist / album tag got one written: putting that back
        // is still a change, even on the same album.
        var changed = undo.Before != undo.After || undo.AlbumArtistFromTag == false || undo.AlbumFromFolder;
        t.GroupUndo = changed ? undo : null;
    }

    /// <summary>
    /// Puts <paramref name="undo"/>'s tags back on <paramref name="t"/> (the library side of
    /// Undo Grouping): album and album artist, and every other tag the grouping changed. Returns
    /// what the file write must also take out (<see cref="IMetadataService.WriteAlbumUndo"/>).
    /// </summary>
    internal static AlbumUndoWrite RestoreTags(Track t, AlbumGroupUndo undo)
    {
        var before = undo.Before;
        var after = undo.After;
        t.Album = before.Album;
        t.AlbumArtist = before.AlbumArtist;
        if (before.TrackNumber != after.TrackNumber) t.TrackNumber = before.TrackNumber;
        if (before.TrackCount != after.TrackCount) t.TrackCount = before.TrackCount;
        if (before.DiscNumber != after.DiscNumber) t.DiscNumber = before.DiscNumber;
        if (before.DiscCount != after.DiscCount) t.DiscCount = before.DiscCount;
        if (before.IsCompilation != after.IsCompilation) t.IsCompilation = before.IsCompilation;
        if (before.IsReleaseTypeOverridden != after.IsReleaseTypeOverridden) t.IsReleaseTypeOverridden = before.IsReleaseTypeOverridden;
        if (before.ReleaseType != after.ReleaseType) t.ReleaseType = before.ReleaseType;
        if (before.Year != after.Year) t.Year = before.Year;
        if (!string.Equals(before.ReleaseDate, after.ReleaseDate, StringComparison.Ordinal)
            && string.Equals(t.ReleaseDate, after.ReleaseDate, StringComparison.Ordinal))
            t.ReleaseDate = before.ReleaseDate;
        return new AlbumUndoWrite(
            undo.Identity,
            RemoveAlbumArtist: undo.AlbumArtistFromTag == false,
            RemoveAlbum: undo.AlbumFromFolder,
            // The writer treats year 0 as unknown and leaves the file's alone (MetadataService).
            RemoveYear: before.Year == 0 && after.Year != 0);
    }

    /// <summary>After the file write: the song's grouping state from before, and no record.</summary>
    internal static void RestoreState(Track t, AlbumGroupUndo undo, bool written)
    {
        // Written: the file has an album artist tag unless the write took it out again.
        t.AlbumArtistFromTag = written ? undo.AlbumArtistFromTag != false : undo.AlbumArtistFromTag;
        t.AlbumFromFolder = undo.AlbumFromFolder;
        t.UngroupedAlbumArtist = undo.UngroupedAlbumArtist;
        t.UngroupedAlbum = undo.UngroupedAlbum;
        t.AlbumId = undo.AlbumId;
        t.GroupUndo = null;
    }

    /// <summary>A rescan read <paramref name="target"/> anew (every tag save re-imports the file):
    /// the record comes along while the file still says what the grouping left.</summary>
    internal static void Carry(Track source, Track target)
    {
        target.GroupUndo = source.GroupUndo is { } undo && Applies(target, undo) ? undo : null;
    }

    /// <summary>Drops the records that no longer apply (edited since). Returns how many.</summary>
    internal static int Prune(IReadOnlyList<Track> tracks)
    {
        var dropped = 0;
        foreach (var t in tracks)
        {
            if (t.GroupUndo is not { } undo || Applies(t, undo)) continue;
            t.GroupUndo = null;
            dropped++;
        }
        return dropped;
    }
}

/// <summary>What Undo Grouping writes into a file besides its tags (<see cref="IMetadataService.WriteAlbumUndo"/>):
/// the album identity tags from before a Move to Album (null: left as they are), and the album
/// artist, album and year tags the grouping wrote into a file that had none.</summary>
public sealed record AlbumUndoWrite(AlbumIdentityTags? Identity, bool RemoveAlbumArtist, bool RemoveAlbum, bool RemoveYear);
