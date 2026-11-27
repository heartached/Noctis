using System.Text.Json.Serialization;
using Noctis.Services;

namespace Noctis.Models;

/// <summary>
/// Undo Grouping (Discord 11-27, Halil: "I grouped two albums, now one is hidden and I don't know
/// how to reverse this"): what a song had before "Group into One Album" (#178) or "Move to Album"
/// (#239) changed it, kept on the song (<see cref="Track.GroupUndo"/>) in library.json so the
/// album menu can put it back after a restart. A song grouped again keeps the values from before
/// the first grouping, so Undo returns it to where it was before any of them. Songs grouped by
/// builds before this have no record and are not offered Undo. See <see cref="AlbumUndo"/>.
/// </summary>
public sealed class AlbumGroupUndo
{
    /// <summary>The song's album tags before the first grouping.</summary>
    public AlbumTagValues Before { get; set; } = AlbumTagValues.Empty;

    /// <summary>The same tags as the last grouping left them. The record applies only while the
    /// song still has these (<see cref="AlbumUndo.Applies(Track)"/>), so Undo never overwrites an
    /// edit made since.</summary>
    public AlbumTagValues After { get; set; } = AlbumTagValues.Empty;

    /// <summary>The song's album key before (<see cref="Track.AlbumId"/>).</summary>
    public Guid AlbumId { get; set; }

    /// <summary><see cref="Track.AlbumArtistFromTag"/> before: false means the file had no album
    /// artist tag, so Undo takes the one the grouping wrote out again.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AlbumArtistFromTag { get; set; }

    /// <summary><see cref="Track.AlbumFromFolder"/> before: the file had no album tag (the name was
    /// its folder's), so Undo takes the written one out again.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AlbumFromFolder { get; set; }

    /// <summary><see cref="Track.UngroupedAlbumArtist"/> before (the automatic grouping's own
    /// record, which a manual grouping makes it forget).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UngroupedAlbumArtist { get; set; }

    /// <summary><see cref="Track.UngroupedAlbum"/> before.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UngroupedAlbum { get; set; }

    /// <summary>The file's tag entries the groupings replaced, as the file had them before the
    /// first (<see cref="RawTagSnapshot"/>): Undo puts exactly these back. Null while no grouping
    /// has reached the file (it could not be written yet), so there is nothing in it to undo.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RawTagSnapshot? Raw { get; set; }
}

/// <summary>The album-level tags Group into One Album and Move to Album change on a song
/// (<see cref="AlbumMove.Plan"/>).</summary>
public sealed record AlbumTagValues(
    string Album,
    string AlbumArtist,
    int TrackNumber,
    int TrackCount,
    int DiscNumber,
    int DiscCount,
    bool IsCompilation,
    bool IsReleaseTypeOverridden,
    ReleaseType ReleaseType,
    int Year,
    string ReleaseDate)
{
    public static readonly AlbumTagValues Empty = new(string.Empty, string.Empty, 0, 0, 0, 0, false, false, default, 0, string.Empty);
}
