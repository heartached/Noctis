using System.Text;
using Noctis.Models;
using Noctis.Services;
using Xunit;
using static Noctis.Tests.ReplayGainWriteTests;

namespace Noctis.Tests;

/// <summary>
/// ExtendedTagIO (rating, dislike, compilation, work/movement, release type override)
/// created ID3v2 and APEv2 on every format, so rating a FLAC gave it a leading ID3v2
/// header and a trailing APEv2 (ffmpeg "invalid sync code") and an MP3 gained APEv2 —
/// the same foreign-tag problem WriteCustomField and WriteField had.
/// </summary>
public class ExtendedTagWriteTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public ExtendedTagWriteTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static int ReadRating(string path)
    {
        using var f = TagLib.File.Create(path);
        return ExtendedTagIO.ReadRating(f);
    }

    private static void AssertNativeOnly(string path, TagLib.TagTypes native)
    {
        var types = TagTypesOnDisk(path);
        Assert.True(types.HasFlag(native), types.ToString());
        if (native != TagLib.TagTypes.Id3v2)
            Assert.False(types.HasFlag(TagLib.TagTypes.Id3v2), types.ToString());
        Assert.False(types.HasFlag(TagLib.TagTypes.Ape), types.ToString());
        Assert.False(Contains(File.ReadAllBytes(path), "APETAGEX"));
    }

    // The metadata editor's save: every ExtendedTagIO writer, through the atomic path.
    private static bool WriteTrack(string path) =>
        new MetadataService().WriteTrackMetadata(new Track
        {
            FilePath = path,
            Title = "Quevedo",
            IsCompilation = true,
            UseWorkAndMovement = true,
            WorkName = "Bzrp Music Sessions",
            MovementName = "Vol. 52",
            MovementNumber = 52,
            MovementCount = 60,
            Rating = 3,
            IsDisliked = true,
        });

    private static void AssertTrackReadBack(string path)
    {
        using var f = TagLib.File.Create(path);
        Assert.True(ExtendedTagIO.ReadIsCompilation(f));
        Assert.True(ExtendedTagIO.ReadUseWorkAndMovement(f));
        Assert.Equal("Bzrp Music Sessions", ExtendedTagIO.ReadWorkName(f));
        Assert.Equal("Vol. 52", ExtendedTagIO.ReadMovementName(f));
        Assert.Equal(52, ExtendedTagIO.ReadMovementNumber(f));
        Assert.Equal(60, ExtendedTagIO.ReadMovementCount(f));
        Assert.Equal(3, ExtendedTagIO.ReadRating(f));
        Assert.True(ExtendedTagIO.ReadIsDisliked(f));
    }

    [Fact]
    public void Flac_rating_leaves_only_the_vorbis_comment()
    {
        var path = CreateFlac(_dir);

        Assert.True(new MetadataService().WriteRating(path, 4, false));

        AssertNativeOnly(path, TagLib.TagTypes.Xiph);
        Assert.False(TagTypesOnDisk(path).HasFlag(TagLib.TagTypes.Id3v1));
        Assert.Equal("fLaC", Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 4));

        using (var f = TagLib.File.Create(path))
        {
            var xiph = (TagLib.Ogg.XiphComment)f.GetTag(TagLib.TagTypes.Xiph, false);
            Assert.Equal("80", xiph.GetFirstField("RATING"));
        }
        Assert.Equal(4, ReadRating(path));
    }

    [Fact]
    public void Mp3_rating_writes_popm_and_no_ape()
    {
        var path = CreateMp3WithId3v2Only(_dir);

        Assert.True(new MetadataService().WriteRating(path, 5, true));

        AssertNativeOnly(path, TagLib.TagTypes.Id3v2);
        using (var f = TagLib.File.Create(path))
        {
            var id3 = (TagLib.Id3v2.Tag)f.GetTag(TagLib.TagTypes.Id3v2, false);
            Assert.Equal(255, id3.GetFrames<TagLib.Id3v2.PopularimeterFrame>().Single().Rating);
            Assert.True(ExtendedTagIO.ReadIsDisliked(f));
        }
        Assert.Equal(5, ReadRating(path));
    }

    [Fact]
    public void Flac_existing_id3v2_rating_is_still_updated()
    {
        // No longer created on FLAC, but a POPM the file already carries (from an older
        // Noctis save or another tagger) must not keep a stale rating.
        var path = CreateFlac(_dir);
        using (var f = TagLib.File.Create(path))
        {
            var id3 = (TagLib.Id3v2.Tag)f.GetTag(TagLib.TagTypes.Id3v2, true);
            TagLib.Id3v2.PopularimeterFrame.Get(id3, "Windows Media Player 9 Series", true).Rating = 1;
            f.Save();
        }

        Assert.True(new MetadataService().WriteRating(path, 4, false));

        using var reread = TagLib.File.Create(path);
        var tag = (TagLib.Id3v2.Tag)reread.GetTag(TagLib.TagTypes.Id3v2, false);
        Assert.Equal(196, tag.GetFrames<TagLib.Id3v2.PopularimeterFrame>().Single().Rating);
        Assert.Equal(4, ExtendedTagIO.ReadRating(reread));
    }

    [Fact]
    public void Mp3_existing_ape_rating_is_still_updated()
    {
        var path = CreateMp3WithId3v2Only(_dir);
        using (var f = TagLib.File.Create(path))
        {
            ((TagLib.Ape.Tag)f.GetTag(TagLib.TagTypes.Ape, true)).SetValue("RATING", "20");
            f.Save();
        }

        Assert.True(new MetadataService().WriteRating(path, 2, false));

        using var reread = TagLib.File.Create(path);
        var ape = (TagLib.Ape.Tag)reread.GetTag(TagLib.TagTypes.Ape, false);
        Assert.Equal("40", ape.GetItem("RATING").ToString());
    }

    [Fact]
    public void Flac_track_save_leaves_only_the_vorbis_comment()
    {
        var path = CreateFlac(_dir);

        Assert.True(WriteTrack(path));

        AssertNativeOnly(path, TagLib.TagTypes.Xiph);
        Assert.Equal("fLaC", Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 4));
        AssertTrackReadBack(path);
    }

    [Fact]
    public void Mp3_track_save_writes_id3v2_and_no_ape()
    {
        var path = CreateMp3WithId3v2Only(_dir);

        Assert.True(WriteTrack(path));

        AssertNativeOnly(path, TagLib.TagTypes.Id3v2);
        AssertTrackReadBack(path);
    }

    [Fact]
    public void Mp3_existing_ape_custom_field_is_still_updated()
    {
        var path = CreateMp3WithId3v2Only(_dir);
        using (var f = TagLib.File.Create(path))
        {
            ((TagLib.Ape.Tag)f.GetTag(TagLib.TagTypes.Ape, true)).SetValue("WORK", "Old Work");
            f.Save();
        }

        Assert.True(WriteTrack(path));

        using var reread = TagLib.File.Create(path);
        var ape = (TagLib.Ape.Tag)reread.GetTag(TagLib.TagTypes.Ape, false);
        Assert.Equal("Bzrp Music Sessions", ape.GetItem("WORK").ToString());
    }
}
