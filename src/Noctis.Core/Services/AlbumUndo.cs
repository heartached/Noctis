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
    /// </summary>
    internal static AlbumGroupUndo Begin(Track t)
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
                Raw = kept.Raw,
            };
        return new AlbumGroupUndo
        {
            Before = Capture(t),
            AlbumId = t.AlbumId,
            AlbumArtistFromTag = t.AlbumArtistFromTag,
            AlbumFromFolder = t.AlbumFromFolder,
            UngroupedAlbumArtist = t.UngroupedAlbumArtist,
            UngroupedAlbum = t.UngroupedAlbum,
        };
    }

    /// <summary>After the grouping changed <paramref name="t"/>: keeps the record, or none when
    /// the song has its own tags again (grouped back where it was). <paramref name="replaced"/>
    /// is what the write replaced in the file (null: the file was not written).</summary>
    internal static void Finish(Track t, AlbumGroupUndo undo, RawTagSnapshot? replaced)
    {
        undo.After = Capture(t);
        if (replaced != null) undo.Raw = Keep(undo.Raw, replaced);
        // A file without its own album artist / album tag got one written: putting that back
        // is still a change, even on the same album.
        var changed = undo.Before != undo.After || undo.AlbumArtistFromTag == false || undo.AlbumFromFolder;
        t.GroupUndo = changed ? undo : null;
    }

    /// <summary>A later write's replaced values added to the record's: an entry an earlier
    /// write replaced keeps its value from before that one.</summary>
    internal static RawTagSnapshot Keep(RawTagSnapshot? earlier, RawTagSnapshot later)
    {
        if (earlier == null) return later;
        earlier.AddNewer(later);
        return earlier;
    }

    /// <summary>
    /// Puts <paramref name="undo"/>'s tags back on <paramref name="t"/> (the library side of
    /// Undo Grouping): album and album artist, and every other tag the grouping changed.
    /// </summary>
    internal static void RestoreTags(Track t, AlbumGroupUndo undo)
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
    }

    /// <summary>
    /// The file write of Undo Grouping: the replaced entries back as they were
    /// (<see cref="AlbumGroupUndo.Raw"/>, after any earlier Undo the file has not taken yet), and
    /// for tag blocks kept by value only, the album tags from before. Null when no grouping
    /// reached the file: there is nothing in it to put back.
    /// </summary>
    internal static AlbumFileWrite? FileWrite(AlbumGroupUndo undo, AlbumFileWrite? pending)
    {
        RawTagSnapshot? raw = null;
        if (pending?.Restore is { } earlier)
        {
            raw = new RawTagSnapshot();
            raw.AddNewer(earlier);
        }
        if (undo.Raw is { } own) raw = Keep(raw, own);
        if (raw == null) return null;
        var before = undo.Before;
        var after = undo.After;
        var fields = AlbumTagFields.Album | AlbumTagFields.AlbumArtist
                     | (before.TrackNumber != after.TrackNumber || before.TrackCount != after.TrackCount
                        || before.DiscNumber != after.DiscNumber || before.DiscCount != after.DiscCount ? AlbumTagFields.Numbering : 0)
                     | (before.Year != after.Year ? AlbumTagFields.Year : 0);
        return new AlbumFileWrite
        {
            Restore = raw,
            RestoreValues = raw.OtherBlocks
                ? new AlbumTagWrite
                {
                    Fields = fields,
                    Album = undo.AlbumFromFolder ? null : before.Album,
                    AlbumArtist = undo.AlbumArtistFromTag == false ? null : before.AlbumArtist,
                    TrackNumber = before.TrackNumber,
                    TrackCount = before.TrackCount,
                    DiscNumber = before.DiscNumber,
                    DiscCount = before.DiscCount,
                    Year = before.Year,
                }
                : null,
        };
    }

    /// <summary>After Undo: the song's grouping state from before, and no record.</summary>
    internal static void RestoreState(Track t, AlbumGroupUndo undo)
    {
        t.AlbumArtistFromTag = undo.AlbumArtistFromTag;
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
