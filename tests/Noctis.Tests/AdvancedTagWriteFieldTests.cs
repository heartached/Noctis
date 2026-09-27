using System.Text;
using Noctis.Services;
using Xunit;
using static Noctis.Tests.ReplayGainWriteTests;

namespace Noctis.Tests;

/// <summary>
/// AdvancedTagIO.WriteField (Lyricist, Publisher, Encoded by, ISRC, Language,
/// Description) created ID3v2 on every format, so an Advanced Details edit gave a FLAC
/// a leading ID3v2 header — the same foreign-tag problem WriteCustomField had.
/// </summary>
public class AdvancedTagWriteFieldTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public AdvancedTagWriteFieldTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static bool WriteFields(string path)
    {
        var original = AdvancedTagIO.ReadAll(path);
        var updated = AdvancedTagIO.ReadAll(path);
        updated.Lyricist = "Bizarrap";
        updated.Publisher = "Dale Play";
        updated.EncodedBy = "Noctis";
        updated.Isrc = "ARF342200123";
        updated.Language = "spa";
        updated.Description = "Session 52";
        // The editor's path: MetadataService's atomic temp-copy save around ApplyAll.
        return ((IMetadataService)new MetadataService()).WriteAdvancedFields(path, updated, original);
    }

    private static void AssertReadBack(string path)
    {
        var fields = AdvancedTagIO.ReadAll(path);
        Assert.Equal("Bizarrap", fields.Lyricist);
        Assert.Equal("Dale Play", fields.Publisher);
        Assert.Equal("Noctis", fields.EncodedBy);
        Assert.Equal("ARF342200123", fields.Isrc);
        Assert.Equal("spa", fields.Language);
        Assert.Equal("Session 52", fields.Description);
    }

    [Fact]
    public void Flac_edit_leaves_only_the_vorbis_comment()
    {
        var path = CreateFlac(_dir);

        Assert.True(WriteFields(path));

        var types = TagTypesOnDisk(path);
        Assert.True(types.HasFlag(TagLib.TagTypes.Xiph), types.ToString());
        Assert.False(types.HasFlag(TagLib.TagTypes.Id3v2), types.ToString());
        Assert.False(types.HasFlag(TagLib.TagTypes.Ape), types.ToString());
        Assert.False(types.HasFlag(TagLib.TagTypes.Id3v1), types.ToString());

        var bytes = File.ReadAllBytes(path);
        Assert.Equal("fLaC", Encoding.ASCII.GetString(bytes, 0, 4)); // no ID3v2 header in front
        Assert.False(Contains(bytes, "APETAGEX"));

        AssertReadBack(path);
    }

    [Fact]
    public void Flac_existing_id3v2_is_still_updated()
    {
        // No longer created on FLAC, but one the file already carries (from an older
        // Noctis save or another tagger) must not keep a stale value.
        var path = CreateFlac(_dir);
        using (var f = TagLib.File.Create(path))
        {
            var id3 = (TagLib.Id3v2.Tag)f.GetTag(TagLib.TagTypes.Id3v2, true);
            id3.AddFrame(new TagLib.Id3v2.TextInformationFrame(TagLib.ByteVector.FromString("TSRC", TagLib.StringType.Latin1))
                { Text = new[] { "OLD000000000" } });
            f.Save();
        }

        Assert.True(WriteFields(path));

        using var reread = TagLib.File.Create(path);
        var tag = (TagLib.Id3v2.Tag)reread.GetTag(TagLib.TagTypes.Id3v2, false);
        var isrc = tag.GetFrames<TagLib.Id3v2.TextInformationFrame>()
            .Single(fr => fr.FrameId == TagLib.ByteVector.FromString("TSRC", TagLib.StringType.Latin1));
        Assert.Equal("ARF342200123", isrc.Text.Single());
    }

    [Fact]
    public void Mp3_edit_writes_id3v2_and_no_ape()
    {
        var path = CreateMp3WithId3v2Only(_dir);

        Assert.True(WriteFields(path));

        var types = TagTypesOnDisk(path);
        Assert.True(types.HasFlag(TagLib.TagTypes.Id3v2), types.ToString());
        Assert.False(types.HasFlag(TagLib.TagTypes.Ape), types.ToString());
        Assert.False(Contains(File.ReadAllBytes(path), "APETAGEX"));

        AssertReadBack(path);
    }
}
