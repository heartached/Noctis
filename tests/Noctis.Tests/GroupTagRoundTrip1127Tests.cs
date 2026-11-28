using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Undo Grouping follow-up (owner 11-27): Group into One Album and Move to Album may change in
/// the FILE only the tags they mean to change (album and album artist; for a move also the
/// numbering, year, compilation / release type and album identity), and Undo Grouping must put
/// the file back exactly as it was. Every tag of every tag block is dumped before grouping, after
/// it and after Undo, over the formats the tag tests use, with values the library holds
/// differently from the file (a "feat." name merged from the title, several composers, an
/// analysed BPM, a rating not in the file).
/// </summary>
[Collection("ArtistCredit global configuration")]
public class GroupTagRoundTrip1127Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _out;

    public GroupTagRoundTrip1127Tests(ITestOutputHelper output)
    {
        _out = output;
        ArtistCredit.ResetToDefaults();
    }

    public void Dispose()
    {
        ArtistCredit.ResetToDefaults();
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch { }
    }

    private string Music => Path.Combine(_root, "music");

    /// <summary>Every format the library takes and TagLib writes (MetadataService.SupportedExtensions
    /// but .dff, which TagLib cannot write); MP3 with ID3v2.3 and with ID3v2.4.</summary>
    public static TheoryData<string, byte> Formats()
    {
        var data = new TheoryData<string, byte> { { "mp3", 3 } };
        foreach (var ext in MetadataService.SupportedExtensions.Where(e => e != ".dff").Order(StringComparer.Ordinal))
            data.Add(ext.TrimStart('.'), (byte)(ext == ".mp3" ? 4 : 0));
        return data;
    }

    [Fact]
    public void EveryWritableLibraryFormat_IsCovered()
        => Assert.Equal(MetadataService.SupportedExtensions.Count, Formats().Count()); // .dff out, MP3 twice

    private string Copy(string folder, string ext, Action<TagLib.File> tag)
    {
        var dir = Path.Combine(Music, folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "01." + ext);
        // As AdvancedReleaseDateWriteTests: the silent fixtures, and hand-built DSF / Monkey's Audio files.
        switch (ext)
        {
            case "dsf": File.WriteAllBytes(path, AdvancedReleaseDateWriteTests.MinimalDsf()); break;
            case "ape": File.WriteAllBytes(path, AdvancedReleaseDateWriteTests.MinimalApe()); break;
            default:
                var fixture = ext switch
                {
                    "oga" => "ogg",
                    "alac" or "mp4" or "m4b" => "m4a",
                    "aif" => "aiff",
                    "asf" => "wma",
                    _ => ext,
                };
                File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Audio", "Silence", "silence." + fixture), path, true);
                break;
        }
        using var f = TagLib.File.Create(path);
        // Every block the format can carry, so each one is checked: WAV's ID3v2 chunk, INFO and
        // MovieID lists and DivX block; the APEv2 tag of WavPack and Monkey's Audio.
        foreach (var type in ext switch
                 {
                     "wav" => new[] { TagLib.TagTypes.Id3v2, TagLib.TagTypes.RiffInfo, TagLib.TagTypes.MovieId, TagLib.TagTypes.DivX },
                     "wv" or "ape" => new[] { TagLib.TagTypes.Ape },
                     _ => Array.Empty<TagLib.TagTypes>(),
                 })
            f.GetTag(type, true);
        tag(f);
        f.Save();
        return path;
    }

    /// <summary>The song whose tags must survive: everything a grouping has no business touching.</summary>
    private static void TagRich(TagLib.File f, byte id3Version)
    {
        if (id3Version > 0 && f.GetTag(TagLib.TagTypes.Id3v2, true) is TagLib.Id3v2.Tag id3)
            id3.Version = id3Version;
        var t = f.Tag;
        t.Title = "Song (feat. Guest)";
        t.Performers = new[] { "Main", "Second" };
        t.AlbumArtists = new[] { "Band", "Friend" };
        t.Album = "Album (Deluxe)";
        t.Genres = new[] { "Rock", "Pop" };
        t.Composers = new[] { "Writer One", "Writer Two" };
        t.Comment = "a comment";
        t.Lyrics = "line one\nline two";
        t.Track = 3; t.TrackCount = 12; t.Disc = 1; t.DiscCount = 2;
        t.Year = 2001;
        t.TitleSort = "Song, The";
        t.PerformersSort = new[] { "Main, The" };
        t.AlbumSort = "Album, The";
        t.AlbumArtistsSort = new[] { "Band, The" };
        t.MusicBrainzReleaseId = "rel-deluxe";
        t.MusicBrainzTrackId = "track-1";
        t.Copyright = "(c) Band";
        t.Pictures = new TagLib.IPicture[]
        {
            new TagLib.Picture(new TagLib.ByteVector(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 }))
                { Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg" },
        };
    }

    private PersistenceService? _persistence;

    private async Task<LibraryService> NewLibraryAsync(int expected)
    {
        var persistence = _persistence = new PersistenceService(Path.Combine(_root, "data"));
        await persistence.SaveSettingsAsync(new AppSettings { MusicFolders = { Music } });
        var library = new LibraryService(new MetadataService(), persistence,
            new SqliteLibraryIndexService(persistence), new NoctisAccountLibraryTests.NoOpAudit());
        await library.ScanAsync(new[] { Music });
        if (library.AllTracks.Count != expected) Assert.Fail(ScanMissReport.Describe(library, Music));
        return library;
    }

    private async Task<LibraryService> RestartAsync()
    {
        var reloaded = new LibraryService(new MetadataService(), _persistence!,
            new SqliteLibraryIndexService(_persistence!), new NoctisAccountLibraryTests.NoOpAudit());
        await reloaded.LoadAsync();
        await reloaded.BackgroundInit;
        return reloaded;
    }

    private string CopyMain(string ext, uint year = 0) => Copy("Main", ext, f =>
    {
        f.Tag.Title = "Opener"; f.Tag.Performers = new[] { "Band" }; f.Tag.AlbumArtists = new[] { "Band" };
        f.Tag.Album = "Album"; f.Tag.Track = 1; f.Tag.TrackCount = 9; f.Tag.Year = year;
    });

    /// <summary>Library values that differ from the file's for app-side reasons.</summary>
    private static void DriftLibrary(Track song)
    {
        song.Bpm = 128;   // an analysis result, not a tag
        song.Rating = 4;  // a rating kept in Noctis only
    }

    // ── Every tag, per block ──

    internal static SortedDictionary<string, string> Dump(string path)
    {
        var d = new SortedDictionary<string, string>(StringComparer.Ordinal);
        using var file = TagLib.File.Create(path);
        d["tagtypes"] = file.TagTypesOnDisk.ToString();
        if (file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
        {
            d["id3:version"] = id3.Version.ToString();
            var n = new Dictionary<string, int>();
            foreach (var frame in id3)
            {
                var key = "id3:" + frame.FrameId.ToString(TagLib.StringType.Latin1);
                if (frame is TagLib.Id3v2.UserTextInformationFrame u) key += ":" + u.Description;
                n[key] = n.GetValueOrDefault(key) + 1;
                // Rendered as ID3v2.4 whatever the tag's version: values are compared, and a v2.2
                // tag (AIFF, DSF) is saved as v2.3 by every Noctis save (TagLibFileTypes.UpgradeId3v22).
                d[key + "#" + n[key]] = Convert.ToHexString(frame.Render(4).Data);
            }
        }
        if (file.GetTag(TagLib.TagTypes.Id3v1) is TagLib.Id3v1.Tag v1)
        {
            // Field by field: the block holds the album too, which a grouping is meant to change.
            d["id3v1:title"] = v1.Title ?? "";
            d["id3v1:artist"] = string.Join(";", v1.Performers);
            d["id3v1:album"] = v1.Album ?? "";
            d["id3v1:year"] = v1.Year.ToString();
            d["id3v1:comment"] = v1.Comment ?? "";
            d["id3v1:track"] = v1.Track.ToString();
            d["id3v1:genre"] = string.Join(";", v1.Genres);
            d["id3v1:raw"] = Convert.ToHexString(v1.Render().Data);
        }
        if (file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
            foreach (var name in xiph)
                d["xiph:" + name.ToUpperInvariant()] = string.Join("␞", xiph.GetField(name));
        if (file.GetTag(TagLib.TagTypes.FlacMetadata) is TagLib.Flac.Metadata flac)
            d["flac:pictures"] = string.Join(",", flac.Pictures.Select(p => Convert.ToHexString(p.Data.Data)));
        if (file.GetTag(TagLib.TagTypes.Apple) is TagLib.Mpeg4.AppleTag apple)
        {
            var n = new Dictionary<string, int>();
            foreach (var box in apple)
            {
                var key = "mp4:" + box.BoxType.ToString(TagLib.StringType.Latin1);
                if (box.BoxType == (TagLib.ByteVector)"----" && box.Children != null)
                    foreach (var child in box.Children)
                        if (child.BoxType == (TagLib.ByteVector)"name" && child is TagLib.Mpeg4.AppleAdditionalInfoBox info)
                            key += ":" + info.Text;
                n[key] = n.GetValueOrDefault(key) + 1;
                d[key + "#" + n[key]] = Convert.ToHexString(box.Render().Data);
            }
        }
        if (file.GetTag(TagLib.TagTypes.Ape) is TagLib.Ape.Tag ape)
            foreach (var key in ape)
                d["ape:" + key.ToUpperInvariant()] = Convert.ToHexString(ape.GetItem(key).Render().Data);
        if (file.GetTag(TagLib.TagTypes.Asf) is TagLib.Asf.Tag asf)
        {
            var cd = asf.ContentDescriptionObject;
            d["asf:c:Title"] = cd?.Title ?? "";
            d["asf:c:Author"] = cd?.Author ?? "";
            d["asf:c:Copyright"] = cd?.Copyright ?? "";
            d["asf:c:Description"] = cd?.Description ?? "";
            d["asf:c:Rating"] = cd?.Rating ?? "";
            var n = new Dictionary<string, int>();
            foreach (var x in asf.ExtendedContentDescriptionObject ?? Enumerable.Empty<TagLib.Asf.ContentDescriptor>())
            {
                var key = "asf:x:" + x.Name;
                n[key] = n.GetValueOrDefault(key) + 1;
                d[key + "#" + n[key]] = Convert.ToHexString(x.Render().Data);
            }
            foreach (var r in asf.MetadataLibraryObject ?? Enumerable.Empty<TagLib.Asf.DescriptionRecord>())
            {
                var key = $"asf:m:{r.LanguageListIndex}:{r.StreamNumber}:{r.Name}";
                n[key] = n.GetValueOrDefault(key) + 1;
                d[key + "#" + n[key]] = Convert.ToHexString(r.Render().Data);
            }
        }
        foreach (var (type, list) in new[] { (TagLib.TagTypes.RiffInfo, "INFO"), (TagLib.TagTypes.MovieId, "MID ") })
            if (file.GetTag(type) is TagLib.Riff.ListTag riff)
            {
                // The rendered list, field by field: id, size, data, pad.
                var data = riff.Render().Data;
                var n = new Dictionary<string, int>();
                for (var at = 0; at + 8 <= data.Length;)
                {
                    var id = System.Text.Encoding.ASCII.GetString(data, at, 4);
                    var size = (int)BitConverter.ToUInt32(data, at + 4);
                    var key = "riff:" + list + ":" + id;
                    n[key] = n.GetValueOrDefault(key) + 1;
                    d[key + "#" + n[key]] = Convert.ToHexString(data, at + 8, Math.Min(size, data.Length - at - 8));
                    at += 8 + size + (size & 1);
                }
            }
        if (file.GetTag(TagLib.TagTypes.DivX) is TagLib.Riff.DivXTag divx)
            d["divx"] = Convert.ToHexString(divx.Render().Data);
        return d;
    }

    private static List<string> Diff(SortedDictionary<string, string> a, SortedDictionary<string, string> b) =>
        a.Keys.Union(b.Keys)
            .Where(k => !a.TryGetValue(k, out var x) | !b.TryGetValue(k, out var y) || x != y)
            .ToList();

    // What each operation is meant to change, by tag key (frame id / field / atom).
    private static readonly string[] AlbumKeys =
    {
        "id3:TALB", "id3:TPE2", "id3v1:album", "id3v1:raw", "xiph:ALBUM", "xiph:ALBUMARTIST", "mp4:©alb", "mp4:aART",
        "ape:ALBUM", "ape:ALBUM ARTIST", "asf:x:WM/AlbumTitle", "asf:x:WM/AlbumArtist",
        // TagLib#'s RIFF INFO album (DIRC) and album artist (IART) fields.
        "riff:INFO:DIRC", "riff:INFO:IART",
    };

    private static readonly string[] MoveKeys =
    {
        "id3v1:year", "id3v1:track", "id3:TRCK", "id3:TPOS", "id3:TDRC", "id3:TYER", "id3:TDAT", "id3:TCMP", "id3:TSOA", "id3:TSO2",
        "id3:TXXX:MusicBrainz Album Id", "id3:TXXX:MusicBrainz Release Group Id", "id3:TXXX:MusicBrainz Album Artist Id",
        "id3:TXXX:NOCTIS_RELEASETYPE",
        "xiph:TRACKNUMBER", "xiph:TRACKTOTAL", "xiph:DISCNUMBER", "xiph:DISCTOTAL", "xiph:DATE", "xiph:COMPILATION",
        "xiph:ALBUMSORT", "xiph:ALBUMARTISTSORT", "xiph:MUSICBRAINZ_ALBUMID", "xiph:MUSICBRAINZ_RELEASEGROUPID",
        "xiph:MUSICBRAINZ_ALBUMARTISTID", "xiph:NOCTIS_RELEASETYPE",
        "mp4:trkn", "mp4:disk", "mp4:©day", "mp4:cpil", "mp4:soal", "mp4:soaa",
        "mp4:----:MusicBrainz Album Id", "mp4:----:MusicBrainz Release Group Id", "mp4:----:MusicBrainz Album Artist Id",
        "mp4:----:NOCTIS_RELEASETYPE",
        "ape:TRACK", "ape:DISC", "ape:YEAR", "ape:COMPILATION", "ape:ALBUMSORT", "ape:ALBUMARTISTSORT",
        "ape:MUSICBRAINZ_ALBUMID", "ape:MUSICBRAINZ_RELEASEGROUPID", "ape:MUSICBRAINZ_ALBUMARTISTID", "ape:NOCTIS_RELEASETYPE",
        "asf:x:WM/TrackNumber", "asf:x:TrackTotal", "asf:x:WM/PartOfSet", "asf:x:WM/Year", "asf:x:WM/IsCompilation",
        "asf:x:WM/AlbumSortOrder", "asf:x:WM/AlbumArtistSortOrder", "asf:x:MusicBrainz/Album Id",
        "asf:x:MusicBrainz/Release Group Id", "asf:x:MusicBrainz/Album Artist Id", "asf:x:NOCTIS_RELEASETYPE",
        // RIFF INFO year (ICRD), track (IPRT) and track total (IFRM); MovieID part numbers; the DivX year.
        "riff:INFO:ICRD", "riff:INFO:IPRT", "riff:INFO:IFRM", "riff:MID :PRT1", "riff:MID :PRT2", "divx",
    };

    /// <summary>A tag version change every Noctis save makes on purpose: ID3v2.2 is saved as
    /// ID3v2.3, which can hold the sort and lyricist frames v2.2 drops (TagLibFileTypes.UpgradeId3v22).</summary>
    private static bool KnownUpgrade(string key, SortedDictionary<string, string> before, SortedDictionary<string, string> after)
        => key == "id3:version" && before.GetValueOrDefault(key) == "2" && after.GetValueOrDefault(key) == "3";

    private static bool AlbumNameKey(string key) => key.Split('#')[0] is "id3:TALB" or "xiph:ALBUM" or "mp4:©alb"
        or "ape:ALBUM" or "asf:x:WM/AlbumTitle" or "riff:INFO:DIRC";

    private static bool Intended(string key, bool move)
    {
        var bare = key.Split('#')[0];
        return AlbumKeys.Contains(bare) || (move && MoveKeys.Contains(bare));
    }

    private void Show(string label, SortedDictionary<string, string> a, SortedDictionary<string, string> b, IEnumerable<string> keys)
    {
        foreach (var k in keys)
            _out.WriteLine($"{label} {k}: {a.GetValueOrDefault(k) ?? "(none)"} -> {b.GetValueOrDefault(k) ?? "(none)"}");
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task GroupIntoOneAlbum_ChangesOnlyAlbumTags_AndUndoRestoresTheFileExactly(string ext, byte id3Version)
    {
        var song = Copy("Deluxe", ext, f => TagRich(f, id3Version));
        Copy("Main", ext, f => { f.Tag.Title = "Opener"; f.Tag.Performers = new[] { "Band" }; f.Tag.AlbumArtists = new[] { "Band" }; f.Tag.Album = "Album"; f.Tag.Track = 1; });
        var library = await NewLibraryAsync(2);
        DriftLibrary(library.AllTracks.Single(t => t.FilePath == song));
        var before = Dump(song);

        await library.GroupIntoAlbumAsync(library.AllTracks.ToList(), "Album", "Band");
        var grouped = Dump(song);
        var groupDiff = Diff(before, grouped);
        Show("group", before, grouped, groupDiff);

        await library.UndoAlbumGroupingAsync(library.AllTracks.ToList());
        var undone = Dump(song);
        var undoDiff = Diff(before, undone);
        Show("undo", before, undone, undoDiff);

        Assert.Contains(groupDiff, AlbumNameKey);
        Assert.Empty(groupDiff.Where(k => !Intended(k, move: false) && !KnownUpgrade(k, before, grouped)));
        Assert.Empty(undoDiff.Where(k => !KnownUpgrade(k, before, undone)));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task MoveToAlbum_ChangesOnlyTheMovesTags_AndUndoRestoresTheFileExactly(string ext, byte id3Version)
    {
        var song = Copy("Deluxe", ext, f => TagRich(f, id3Version));
        Copy("Main", ext, f =>
        {
            f.Tag.Title = "Opener"; f.Tag.Performers = new[] { "Band" }; f.Tag.AlbumArtists = new[] { "Band" };
            f.Tag.Album = "Album"; f.Tag.Track = 1; f.Tag.TrackCount = 9; f.Tag.Year = 2010;
            f.Tag.MusicBrainzReleaseId = "rel-main";
        });
        var library = await NewLibraryAsync(2);
        var track = library.AllTracks.Single(t => t.FilePath == song);
        DriftLibrary(track);
        var before = Dump(song);

        var result = await library.MoveToAlbumAsync(new[] { track }, "Album", "Band");
        Assert.Empty(result.UnwrittenFiles);
        var moved = Dump(song);
        var moveDiff = Diff(before, moved);
        Show("move", before, moved, moveDiff);

        await library.UndoAlbumGroupingAsync(library.AllTracks.ToList());
        var undone = Dump(song);
        var undoDiff = Diff(before, undone);
        Show("undo", before, undone, undoDiff);

        Assert.Contains(moveDiff, AlbumNameKey);
        Assert.Empty(moveDiff.Where(k => !Intended(k, move: true) && !KnownUpgrade(k, before, moved)));
        Assert.Empty(undoDiff.Where(k => !KnownUpgrade(k, before, undone)));
    }

    // ── A file that could not be written then: the next pass writes exactly the same ──

    [Theory]
    [InlineData("mp3", (byte)4)]
    [InlineData("flac", (byte)0)]
    public async Task GroupWriteThatFailed_IsWrittenAfterARestart_AlbumTagsOnly_AndUndoesExactly(string ext, byte id3Version)
    {
        var song = Copy("Deluxe", ext, f => TagRich(f, id3Version));
        CopyMain(ext);
        var library = await NewLibraryAsync(2);
        DriftLibrary(library.AllTracks.Single(t => t.FilePath == song));
        var before = Dump(song);
        File.SetAttributes(song, FileAttributes.ReadOnly);

        var result = await library.GroupIntoAlbumAsync(library.AllTracks.ToList(), "Album", "Band");

        Assert.Single(result.UnwrittenFiles);
        Assert.Empty(Diff(before, Dump(song)));
        Assert.NotNull(library.AllTracks.Single(t => t.FilePath == song).PendingAlbumWrite);

        File.SetAttributes(song, FileAttributes.Normal);
        var reloaded = await RestartAsync(); // the pending write was saved with the library
        var grouped = Dump(song);
        var diff = Diff(before, grouped);
        Show("retry", before, grouped, diff);
        Assert.Contains(diff, k => k.StartsWith("id3:TALB") || k.StartsWith("xiph:ALBUM"));
        Assert.Empty(diff.Where(k => !Intended(k, move: false)));
        var track = reloaded.AllTracks.Single(t => t.FilePath == song);
        Assert.Null(track.PendingAlbumWrite);
        Assert.False(track.AlbumGroupingUnwritten);
        Assert.NotNull(track.GroupUndo?.Raw); // what the late write replaced, for Undo

        await reloaded.UndoAlbumGroupingAsync(reloaded.AllTracks.ToList());
        Assert.Empty(Diff(before, Dump(song)));
    }

    [Fact]
    public async Task MoveWriteThatFailed_IsWrittenLater_WithTheMovesNumberingAndYear()
    {
        var song = Copy("Deluxe", "mp3", f => TagRich(f, 4));
        CopyMain("mp3", year: 2010);
        var library = await NewLibraryAsync(2);
        var before = Dump(song);
        File.SetAttributes(song, FileAttributes.ReadOnly);

        await library.MoveToAlbumAsync(new[] { library.AllTracks.Single(t => t.FilePath == song) }, "Album", "Band");
        Assert.Empty(Diff(before, Dump(song)));

        File.SetAttributes(song, FileAttributes.Normal);
        await library.SaveAlbumGroupingToFilesAsync();

        var diff = Diff(before, Dump(song));
        Assert.Empty(diff.Where(k => !Intended(k, move: true)));
        using var f = TagLib.File.Create(song);
        Assert.Equal(("Album", 2u, 9u, 2010u), (f.Tag.Album, f.Tag.Track, f.Tag.TrackCount, f.Tag.Year));
        Assert.Equal(new[] { "Band" }, f.Tag.AlbumArtists);
    }

    [Theory]
    [InlineData("mp3", (byte)4)]
    [InlineData("flac", (byte)0)]
    [InlineData("m4a", (byte)0)]
    public async Task UndoWriteThatFailed_IsWrittenAfterARestart_AndTakesOutTheTagTheFileNeverHad(string ext, byte id3Version)
    {
        // No album artist tag of its own: the grouping writes one, and Undo must take it out.
        var song = Copy("Deluxe", ext, f => { TagRich(f, id3Version); f.Tag.AlbumArtists = Array.Empty<string>(); });
        CopyMain(ext);
        var library = await NewLibraryAsync(2);
        var before = Dump(song);
        await library.GroupIntoAlbumAsync(library.AllTracks.ToList(), "Album", "Band");
        using (var f = TagLib.File.Create(song)) Assert.Equal(new[] { "Band" }, f.Tag.AlbumArtists);
        File.SetAttributes(song, FileAttributes.ReadOnly);

        var undone = await library.UndoAlbumGroupingAsync(library.AllTracks.ToList());

        Assert.Single(undone.UnwrittenFiles);
        var track = library.AllTracks.Single(t => t.FilePath == song);
        Assert.Equal("Album (Deluxe)", track.Album);       // the library is back at once
        Assert.True(track.AlbumGroupingUnwritten);
        Assert.NotNull(track.PendingAlbumWrite?.Restore);

        File.SetAttributes(song, FileAttributes.Normal);
        var reloaded = await RestartAsync();
        var after = Dump(song);
        var diff = Diff(before, after);
        Show("undo retry", before, after, diff);
        Assert.Empty(diff);
        using (var f = TagLib.File.Create(song)) Assert.Empty(f.Tag.AlbumArtists);
        var back = reloaded.AllTracks.Single(t => t.FilePath == song);
        Assert.Null(back.PendingAlbumWrite);
        Assert.False(back.AlbumGroupingUnwritten);
        Assert.Equal("Album (Deluxe)", back.Album);
    }
}
