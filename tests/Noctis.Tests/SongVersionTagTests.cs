using System.Buffers.Binary;
using Noctis.Models;
using Noctis.Services;
using Xunit;
using static Noctis.Tests.ReplayGainWriteTests;

namespace Noctis.Tests;

/// <summary>
/// Discord (coleman, 11-19): a song's version ("Acoustic", "Live") kept in a tag of its own
/// instead of the title. One VERSION key in every format (Vorbis/Opus VERSION, ID3v2
/// TXXX:VERSION, MP4 ----:com.apple.iTunes:VERSION); TIT3 / SUBTITLE stay Advanced ›
/// Description's. Real temp files only.
/// </summary>
public class SongVersionTagTests : IDisposable
{
    private readonly FolderMetadataBackfillTests.BackfillTestPersistence _persistence = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public SongVersionTagTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _persistence.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Create(string format)
    {
        var dir = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = format switch
        {
            "flac" => CreateFlac(dir),
            "mp3" => CreateMp3WithId3v2Only(dir),
            "m4a" => MultiGenreTagTests.CreateM4a(dir),
            "opus" => CreateOpus(dir),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        using var f = TagLib.File.Create(path);
        f.Tag.Title = "Wonderwall";
        f.Tag.Performers = new[] { "Oasis" };
        f.Tag.Album = "Album";
        f.Save();
        return path;
    }

    /// <summary>The VERSION value exactly where the format keeps it; null when absent.</summary>
    private static string? RawVersion(string path)
    {
        using var f = TagLib.File.Create(path);
        return Path.GetExtension(path) switch
        {
            ".flac" or ".opus" => ((TagLib.Ogg.XiphComment)f.GetTag(TagLib.TagTypes.Xiph, false)).GetField("VERSION").FirstOrDefault(),
            ".mp3" => TagLib.Id3v2.UserTextInformationFrame.Get((TagLib.Id3v2.Tag)f.GetTag(TagLib.TagTypes.Id3v2, false), "VERSION", false)?.Text.FirstOrDefault(),
            ".m4a" => ((TagLib.Mpeg4.AppleTag)f.GetTag(TagLib.TagTypes.Apple, false)).GetDashBox("com.apple.iTunes", "VERSION"),
            _ => throw new ArgumentOutOfRangeException(nameof(path)),
        };
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("flac")]
    [InlineData("m4a")]
    [InlineData("opus")]
    public void Version_IsWritten_ReRead_AndCleared(string format)
    {
        var path = Create(format);
        var meta = new MetadataService();
        var track = meta.ReadTrackMetadata(path)!;
        Assert.Equal("", track.Version);
        Assert.Equal("", meta.ReadVersion(path));

        track.Version = "Acoustic";
        Assert.True(meta.WriteTrackMetadata(track));
        Assert.Equal("Acoustic", RawVersion(path));
        Assert.Equal("Acoustic", meta.ReadTrackMetadata(path)!.Version);
        Assert.Equal("Acoustic", meta.ReadVersion(path));
        Assert.Equal("Wonderwall", meta.ReadTrackMetadata(path)!.Title);   // the title never carries it

        // An empty row (indexed before the field existed) never erases the file's version.
        track.Version = "";
        Assert.True(meta.WriteTrackMetadata(track));
        Assert.Equal("Acoustic", meta.ReadVersion(path));

        Assert.True(meta.ClearVersion(path));
        Assert.True(string.IsNullOrEmpty(RawVersion(path)));
        Assert.Equal("", meta.ReadVersion(path));
        Assert.Equal("", meta.ReadTrackMetadata(path)!.Version);
    }

    [Fact]
    public void Mp3_VersionAndDescription_AreSeparateFields()
    {
        // Advanced › Description already owns TIT3; the version must not land there.
        var path = Create("mp3");
        var meta = new MetadataService();
        var track = meta.ReadTrackMetadata(path)!;
        track.Version = "Live";
        Assert.True(meta.WriteTrackMetadata(track));

        var adv = AdvancedTagIO.ReadAll(path);
        Assert.Equal("", adv.Description);
        Assert.DoesNotContain(adv.CustomTags, kv => kv.Key.Equals("VERSION", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Flac_VersionWrittenByAnotherTagger_IsReadByTheFastPathAndTheFullRead()
    {
        var path = Create("flac");
        using (var f = TagLib.File.Create(path))
        {
            ((TagLib.Ogg.XiphComment)f.GetTag(TagLib.TagTypes.Xiph, true)).SetField("version", " Remastered 2011 ");
            f.Save();
        }
        using (var stream = File.OpenRead(path))
        {
            Assert.True(ExtendedTagIO.TryReadFlacVersion(stream, out var fast));
            Assert.Equal("Remastered 2011", fast);
        }
        var meta = new MetadataService();
        Assert.Equal("Remastered 2011", meta.ReadVersion(path));
        Assert.Equal("Remastered 2011", meta.ReadTrackMetadata(path)!.Version);
        // Not offered as a custom tag in Advanced: it has its own box now.
        Assert.DoesNotContain(AdvancedTagIO.ReadAll(path).CustomTags, kv => kv.Key.Equals("VERSION", StringComparison.OrdinalIgnoreCase));
    }

    // ── Library: kept in library.json, read once for libraries indexed before the field ──

    private static async Task WaitUntil(Func<bool> condition, int budgetMs = 30000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline && !condition())
            await Task.Delay(50);
    }

    private static Track Indexed(string path) => new()
    {
        Id = Guid.NewGuid(),
        FilePath = path,
        Title = "Wonderwall",
        Artist = "Oasis",
        AlbumArtist = "Oasis",
        Album = "Album",
        AlbumId = Track.ComputeAlbumId("Oasis", "Album"),
        Duration = TimeSpan.FromMinutes(4),
        FileSize = 1000,
        SourceType = SourceType.Local,
        LastModified = DateTime.UtcNow,
        DateAdded = DateTime.UtcNow,
    };

    [Fact]
    public async Task Load_V13Library_ReadsTheVersionOfALocalFile_WithoutARescan()
    {
        var path = Create("flac");
        using (var f = TagLib.File.Create(path))
        {
            ((TagLib.Ogg.XiphComment)f.GetTag(TagLib.TagTypes.Xiph, true)).SetField("VERSION", "Acoustic");
            f.Save();
        }
        var track = Indexed(path);
        var stamp = track.LastModified;
        _persistence.LibraryTracks.Add(track);
        _persistence.Settings.MetadataSchemaVersion = 13;

        var library = new LibraryService(new MetadataService(), _persistence, new SqliteLibraryIndexService(_persistence),
            new FolderMetadataBackfillTests.FakeAuditTrail());
        await library.LoadAsync();
        await WaitUntil(() => track.Version.Length > 0 && _persistence.Settings.MetadataSchemaVersion == 14);

        Assert.Equal("Acoustic", track.Version);
        Assert.Equal(14, _persistence.Settings.MetadataSchemaVersion);
        Assert.Equal(stamp, track.LastModified);   // no forced rescan
    }

    [Fact]
    public void Version_PersistsInLibraryJson_AndAnOldRowLoadsEmpty()
    {
        var track = Indexed("C:/m/a.flac");
        track.Version = "Live";
        var json = System.Text.Json.JsonSerializer.Serialize(track);
        Assert.Equal("Live", System.Text.Json.JsonSerializer.Deserialize<Track>(json)!.Version);

        var old = System.Text.Json.JsonSerializer.Deserialize<Track>("""{"Title":"Wonderwall"}""")!;
        Assert.Equal("", old.Version);
    }

    [Fact]
    public void CopyTagsFrom_CarriesTheVersion()
    {
        var playing = Indexed("C:/m/a.flac");
        var edited = Indexed("C:/m/a.flac");
        edited.Version = "Acoustic";
        Assert.True(playing.CopyTagsFrom(edited));
        Assert.Equal("Acoustic", playing.Version);
    }

    // ── A minimal Ogg Opus file TagLib# opens: OpusHead, OpusTags, one audio page ──

    private static string CreateOpus(string dir)
    {
        var head = Concat("OpusHead"u8.ToArray(), [1, 1], U16(312), U32(48000), U16(0), [0]);
        var tags = Concat("OpusTags"u8.ToArray(), U32(4), "test"u8.ToArray(), U32(0));
        var bytes = Concat(
            OggPage(0x02, 0, 0, head),
            OggPage(0x00, 0, 1, tags),
            OggPage(0x04, 312 + 960, 2, [0xF8]));
        var path = Path.Combine(dir, "song.opus");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] OggPage(byte flags, long granule, uint seq, byte[] packet)
    {
        var lacing = new List<byte>();
        for (var n = packet.Length; ; n -= 255)
        {
            lacing.Add((byte)Math.Min(n, 255));
            if (n < 255) break;
        }
        var page = Concat("OggS"u8.ToArray(), [0, flags], new byte[8], U32(7), U32(seq), new byte[4],
            [(byte)lacing.Count], lacing.ToArray(), packet);
        BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6), granule);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), ContainerGaplessInfo.OggCrc(page, crcOffset: 22));
        return page;
    }

    private static byte[] U16(ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); return b; }
    private static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); return b; }
    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}
