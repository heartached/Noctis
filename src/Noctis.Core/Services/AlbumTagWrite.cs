using System.Text.Json.Serialization;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>Which album tags an <see cref="AlbumTagWrite"/> sets.</summary>
[Flags]
public enum AlbumTagFields
{
    None = 0,
    Album = 1,
    AlbumArtist = 2,
    /// <summary>Track and disc number and their totals.</summary>
    Numbering = 4,
    Compilation = 8,
    /// <summary>The release-type choice (<see cref="ExtendedTagIO.WriteReleaseTypeOverride"/>).</summary>
    ReleaseType = 16,
    /// <summary>Year and full release date.</summary>
    Year = 32,
    /// <summary>Album sort names and MusicBrainz release ids (<see cref="AlbumIdentityTags"/>).</summary>
    Identity = 64,
}

/// <summary>
/// Undo Grouping follow-up (owner 11-27): exactly the tags Group into One Album (album, album
/// artist) or Move to Album (also numbering, compilation, release type, year, identity) put in a
/// file, and nothing else. Both used to save the whole song through the Edit Info writer, which
/// also rewrote the artist (with "feat." names merged from the title), kept only the first
/// composer, and wrote an analysed BPM and a Noctis-only rating into the file. Kept on the song
/// while the file could not be written (<see cref="AlbumFileWrite"/>), so the retry writes the
/// same set.
/// </summary>
public sealed record AlbumTagWrite
{
    public AlbumTagFields Fields { get; init; }
    public string? Album { get; init; }
    public string? AlbumArtist { get; init; }
    public int TrackNumber { get; init; }
    public int TrackCount { get; init; }
    public int DiscNumber { get; init; }
    public int DiscCount { get; init; }
    public bool IsCompilation { get; init; }
    /// <summary>Null: no release-type choice (the tag is removed).</summary>
    public ReleaseType? ReleaseTypeOverride { get; init; }
    public int Year { get; init; }
    public string? ReleaseDate { get; init; }
    /// <summary>Null with <see cref="AlbumTagFields.Identity"/>: removed (<see cref="AlbumIdentityTags.None"/>).</summary>
    public AlbumIdentityTags? Identity { get; init; }

    /// <summary>Group into One Album: album and album artist.</summary>
    public static AlbumTagWrite Group(string album, string albumArtist) => new()
    {
        Fields = AlbumTagFields.Album | AlbumTagFields.AlbumArtist,
        Album = album,
        AlbumArtist = albumArtist,
    };

    /// <summary>Move to Album: the plan's values; its year only when it has one (a song keeps its
    /// own otherwise), the identity tags only when known.</summary>
    public static AlbumTagWrite Move(AlbumMove.Plan plan, AlbumIdentityTags? identity) => new()
    {
        Fields = AlbumTagFields.Album | AlbumTagFields.AlbumArtist | AlbumTagFields.Numbering
                 | AlbumTagFields.Compilation | AlbumTagFields.ReleaseType
                 | (plan.Year > 0 ? AlbumTagFields.Year : 0)
                 | (identity != null ? AlbumTagFields.Identity : 0),
        Album = plan.Album,
        AlbumArtist = plan.AlbumArtist,
        TrackNumber = plan.TrackNumber,
        TrackCount = plan.TrackCount,
        DiscNumber = plan.DiscNumber,
        DiscCount = plan.DiscCount,
        IsCompilation = plan.IsCompilation,
        ReleaseTypeOverride = plan.IsReleaseTypeOverridden ? plan.ReleaseType : null,
        Year = plan.Year,
        ReleaseDate = plan.ReleaseDate,
        Identity = identity,
    };

    /// <summary>This write followed by <paramref name="later"/> (its fields win), for a song
    /// grouped again before its file took the first grouping.</summary>
    public AlbumTagWrite Then(AlbumTagWrite later)
    {
        var f = later.Fields;
        return new AlbumTagWrite
        {
            Fields = Fields | f,
            Album = f.HasFlag(AlbumTagFields.Album) ? later.Album : Album,
            AlbumArtist = f.HasFlag(AlbumTagFields.AlbumArtist) ? later.AlbumArtist : AlbumArtist,
            TrackNumber = f.HasFlag(AlbumTagFields.Numbering) ? later.TrackNumber : TrackNumber,
            TrackCount = f.HasFlag(AlbumTagFields.Numbering) ? later.TrackCount : TrackCount,
            DiscNumber = f.HasFlag(AlbumTagFields.Numbering) ? later.DiscNumber : DiscNumber,
            DiscCount = f.HasFlag(AlbumTagFields.Numbering) ? later.DiscCount : DiscCount,
            IsCompilation = f.HasFlag(AlbumTagFields.Compilation) ? later.IsCompilation : IsCompilation,
            ReleaseTypeOverride = f.HasFlag(AlbumTagFields.ReleaseType) ? later.ReleaseTypeOverride : ReleaseTypeOverride,
            Year = f.HasFlag(AlbumTagFields.Year) ? later.Year : Year,
            ReleaseDate = f.HasFlag(AlbumTagFields.Year) ? later.ReleaseDate : ReleaseDate,
            Identity = f.HasFlag(AlbumTagFields.Identity) ? later.Identity : Identity,
        };
    }
}

/// <summary>
/// The tag values a write replaced in a file, as the file had them (Undo Grouping puts them back
/// exactly). Per tag block: ID3v2 frames by frame id (TXXX by description) as rendered bytes
/// (MP3, AAC, AIFF, WAV, DSF), the whole ID3v1 block, Vorbis comment fields (FLAC, Ogg, Opus),
/// MP4 atoms, APEv2 items (WavPack, Monkey's Audio, MP3/AAC with an APE tag), ASF descriptors
/// (WMA), RIFF INFO / MovieID fields and the DivX block (WAV). Only the entries the write changed
/// are kept; an empty value means the file had no such entry (Undo removes it).
/// </summary>
public sealed class RawTagSnapshot
{
    /// <summary>ID3v2 version the frames were rendered for.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public byte Id3v2Version { get; set; }

    /// <summary>ID3v2 frames by key, each base64 of its rendered bytes.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, List<string>>? Id3v2 { get; set; }

    /// <summary>The ID3v1 block (base64), "" for none; null when the write left it alone.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id3v1 { get; set; }

    /// <summary>Vorbis comment fields (FLAC, Ogg, Opus) by name.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string[]>? Xiph { get; set; }

    /// <summary>MP4 atoms by type ("----:mean:name" for freeform ones): data boxes as
    /// "flags:base64" (freeform: their text).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, List<string>>? Apple { get; set; }

    /// <summary>APEv2 items by key, each base64 of its rendered bytes ("" for none).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Ape { get; set; }

    /// <summary>ASF entries: "c:Title" etc. (content description), "x:name" (extended content
    /// descriptors), "m:language:stream:name" (metadata library records); each value
    /// "type:data".</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, List<string>>? Asf { get; set; }

    /// <summary>RIFF list fields: "INFO:IART", "MID :TITL"; each value base64.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, List<string>>? Riff { get; set; }

    /// <summary>The DivX block (base64), "" for none; null when the write left it alone.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DivX { get; set; }

    /// <summary>The file has tag blocks none of the above covers: Undo writes the album tags of
    /// those from the song's values before.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool OtherBlocks { get; set; }

    [JsonIgnore]
    public bool IsEmpty => (Id3v2?.Count ?? 0) == 0 && Id3v1 == null && (Xiph?.Count ?? 0) == 0
                           && (Apple?.Count ?? 0) == 0 && (Ape?.Count ?? 0) == 0 && (Asf?.Count ?? 0) == 0
                           && (Riff?.Count ?? 0) == 0 && DivX == null && !OtherBlocks;

    /// <summary>Adds <paramref name="newer"/>'s entries this one does not have: what an earlier
    /// write replaced stays the original.</summary>
    public void AddNewer(RawTagSnapshot newer)
    {
        if (newer.Id3v2 != null)
        {
            Id3v2 ??= new();
            if (Id3v2Version == 0) Id3v2Version = newer.Id3v2Version;
            foreach (var (k, v) in newer.Id3v2) Id3v2.TryAdd(k, v);
        }
        Id3v1 ??= newer.Id3v1;
        if (newer.Xiph != null)
        {
            Xiph ??= new();
            foreach (var (k, v) in newer.Xiph) Xiph.TryAdd(k, v);
        }
        if (newer.Apple != null)
        {
            Apple ??= new();
            foreach (var (k, v) in newer.Apple) Apple.TryAdd(k, v);
        }
        if (newer.Ape != null)
        {
            Ape ??= new();
            foreach (var (k, v) in newer.Ape) Ape.TryAdd(k, v);
        }
        if (newer.Asf != null)
        {
            Asf ??= new();
            foreach (var (k, v) in newer.Asf) Asf.TryAdd(k, v);
        }
        if (newer.Riff != null)
        {
            Riff ??= new();
            foreach (var (k, v) in newer.Riff) Riff.TryAdd(k, v);
        }
        DivX ??= newer.DivX;
        OtherBlocks |= newer.OtherBlocks;
    }
}

/// <summary>
/// A grouping or Undo Grouping write that has not reached the song's file yet (read-only, held
/// open, missing): kept on the song (<see cref="Track.PendingAlbumWrite"/>) and written by the
/// next pass (at startup, or Settings › Library › Save to Files Now), so the file ends up exactly
/// as the finished write would have left it.
/// </summary>
public sealed class AlbumFileWrite
{
    /// <summary>A grouping's tags (<see cref="AlbumTagWrite"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AlbumTagWrite? Tags { get; set; }

    /// <summary>Undo Grouping: the original values to put back.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RawTagSnapshot? Restore { get; set; }

    /// <summary>Undo Grouping: the album tags from before, for blocks kept by value only
    /// (<see cref="RawTagSnapshot.OtherBlocks"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AlbumTagWrite? RestoreValues { get; set; }
}
