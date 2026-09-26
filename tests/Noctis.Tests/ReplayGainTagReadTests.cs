using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The player's ReplayGain reader only looked at ID3v2, Xiph and MP4 tags. WMA files carry
/// only an ASF tag (where the in-app scanner writes), and mp3gain / foobar2000 / WavPack
/// store RG in APEv2 — both read back as "no tag", so ReplayGain silently did nothing.
/// </summary>
public class ReplayGainTagReadTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public ReplayGainTagReadTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // A run of silent MPEG-1 Layer III frames (128 kbps, 44.1 kHz): enough for TagLib#
    // to find the audio header and treat the file as an MP3.
    private string CreateMp3()
    {
        var path = Path.Combine(_dir, "rg.mp3");
        const int frameLength = 417; // 144 * 128000 / 44100, no padding
        var bytes = new byte[frameLength * 20];
        for (var i = 0; i < bytes.Length; i += frameLength)
        {
            bytes[i] = 0xFF;
            bytes[i + 1] = 0xFB;
            bytes[i + 2] = 0x90;
            bytes[i + 3] = 0x00;
        }
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // An ASF header object holding only the (spec-mandatory, empty) header extension
    // object — TagLib# opens it as a WMA file.
    private string CreateWma()
    {
        var path = Path.Combine(_dir, "rg.wma");
        var bytes = new byte[76];
        new Guid("75B22630-668E-11CF-A6D9-00AA0062CE6C").ToByteArray().CopyTo(bytes, 0);
        BitConverter.GetBytes(76UL).CopyTo(bytes, 16);
        BitConverter.GetBytes(1U).CopyTo(bytes, 24);
        bytes[28] = 1;
        bytes[29] = 2;
        new Guid("5FBF03B5-A92E-11CF-8EE3-00C00C205365").ToByteArray().CopyTo(bytes, 30);
        BitConverter.GetBytes(46UL).CopyTo(bytes, 46);
        new Guid("ABD3D211-A9BA-11CF-8EE6-00C00C205365").ToByteArray().CopyTo(bytes, 54);
        BitConverter.GetBytes((ushort)6).CopyTo(bytes, 70);
        BitConverter.GetBytes(0U).CopyTo(bytes, 72);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void Mp3_with_ApeV2_only_replaygain_is_read()
    {
        var path = CreateMp3();
        using (var f = TagLib.File.Create(path))
        {
            var ape = (TagLib.Ape.Tag)f.GetTag(TagLib.TagTypes.Ape, true);
            ape.SetValue("REPLAYGAIN_TRACK_GAIN", "-7.84 dB");
            ape.SetValue("REPLAYGAIN_ALBUM_GAIN", "-6.10 dB");
            f.Save();
        }

        var (track, album) = VlcAudioPlayer.ReadReplayGainTags(path);

        Assert.Equal(-7.84, track);
        Assert.Equal(-6.10, album);
    }

    [Fact]
    public void Wma_tagged_by_the_scanner_write_path_is_read()
    {
        var path = CreateWma();
        using (var f = TagLib.File.Create(path))
        {
            // The same call ReplayGainScannerService makes: on a WMA it lands in the ASF tag.
            AdvancedTagIO.WriteCustomField(f, "REPLAYGAIN_TRACK_GAIN", "-4.20 dB");
            AdvancedTagIO.WriteCustomField(f, "REPLAYGAIN_ALBUM_GAIN", "-3.50 dB");
            f.Save();
        }

        var (track, album) = VlcAudioPlayer.ReadReplayGainTags(path);

        Assert.Equal(-4.20, track);
        Assert.Equal(-3.50, album);
    }

    [Fact]
    public void Wma_with_lowercase_descriptor_names_is_read()
    {
        var path = CreateWma();
        using (var f = TagLib.File.Create(path))
        {
            var asf = (TagLib.Asf.Tag)f.GetTag(TagLib.TagTypes.Asf, true);
            asf.SetDescriptorString("+1.25 dB", "replaygain_track_gain");
            f.Save();
        }

        var (track, album) = VlcAudioPlayer.ReadReplayGainTags(path);

        Assert.Equal(1.25, track);
        Assert.Null(album);
    }
}
