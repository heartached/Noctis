using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

public class AudioQualityBadgeTests
{
    private static Track MakeTrack(
        string codec = "", string filePath = "song.bin",
        int bitrate = 0, int sampleRate = 0, int bitsPerSample = 0) => new()
    {
        Codec = codec,
        FilePath = filePath,
        Bitrate = bitrate,
        SampleRate = sampleRate,
        BitsPerSample = bitsPerSample,
    };

    // ── Lossless badges (unchanged behavior) ──

    [Fact]
    public void FlacTrack_ShowsLosslessBadge()
    {
        var t = MakeTrack(codec: "FLAC", filePath: "song.flac", sampleRate: 44100, bitsPerSample: 16);
        Assert.Equal("Lossless", t.AudioQualityBadge);
    }

    [Fact]
    public void HiResFlacTrack_ShowsHiResLosslessBadge()
    {
        var t = MakeTrack(codec: "FLAC", filePath: "song.flac", sampleRate: 96000, bitsPerSample: 24);
        Assert.Equal("Hi-Res Lossless", t.AudioQualityBadge);
    }

    [Fact]
    public void AlacM4aTrack_ShowsLosslessBadge()
    {
        var t = MakeTrack(codec: "MPEG-4 Audio (alac)", filePath: "song.m4a", sampleRate: 44100, bitsPerSample: 16);
        Assert.Equal("Lossless", t.AudioQualityBadge);
    }

    // ── Lossy codec badges ──

    [Theory]
    [InlineData("MPEG Version 1 Audio, Layer 3", "song.mp3", "MP3")]
    [InlineData("MPEG-4 Audio (mp4a)", "song.m4a", "AAC")]
    [InlineData("Opus Version 1 Audio", "song.opus", "OPUS")]
    [InlineData("Vorbis Version 0 Audio", "song.ogg", "OGG")]
    [InlineData("Microsoft WMA2 Audio", "song.wma", "WMA")]
    public void LossyTrack_ShowsCodecBadge(string codec, string path, string expected)
    {
        var t = MakeTrack(codec: codec, filePath: path, bitrate: 256, sampleRate: 44100);
        Assert.Equal(expected, t.AudioQualityBadge);
    }

    [Theory]
    [InlineData("song.mp3", "MP3")]
    [InlineData("song.aac", "AAC")]
    [InlineData("song.m4a", "AAC")] // no codec info: m4a defaults to AAC, not ALAC
    [InlineData("song.opus", "OPUS")]
    [InlineData("song.ogg", "OGG")]
    [InlineData("song.wma", "WMA")]
    public void LossyTrack_NoCodecString_FallsBackToExtension(string path, string expected)
    {
        var t = MakeTrack(filePath: path, bitrate: 192, sampleRate: 44100);
        Assert.Equal(expected, t.AudioQualityBadge);
    }

    [Fact]
    public void UnknownFormat_ShowsNoBadge()
    {
        var t = MakeTrack(filePath: "song.dsf", bitrate: 5645, sampleRate: 2822400);
        Assert.Equal(string.Empty, t.AudioQualityBadge);
    }

    // ── Tooltip detail / description ──

    [Fact]
    public void LossyTrack_DetailedInfo_IncludesBitrate()
    {
        var t = MakeTrack(codec: "MPEG-4 Audio (mp4a)", filePath: "song.m4a", bitrate: 256, sampleRate: 44100);
        Assert.Contains("256 kbps", t.AudioQualityDetailedInfo);
        Assert.Contains("AAC", t.AudioQualityDetailedInfo);
    }

    [Fact]
    public void LosslessTrack_DetailedInfo_OmitsBitrate()
    {
        var t = MakeTrack(codec: "FLAC", filePath: "song.flac", bitrate: 1024, sampleRate: 44100, bitsPerSample: 16);
        Assert.DoesNotContain("kbps", t.AudioQualityDetailedInfo);
    }

    [Fact]
    public void AudioQualityDescription_MatchesBadgeKind()
    {
        var lossless = MakeTrack(codec: "FLAC", filePath: "song.flac");
        var lossy = MakeTrack(filePath: "song.mp3", bitrate: 320);
        var unknown = MakeTrack(filePath: "song.dsf");
        Assert.Contains("Lossless", lossless.AudioQualityDescription);
        Assert.Contains("Compressed", lossy.AudioQualityDescription);
        Assert.Equal(string.Empty, unknown.AudioQualityDescription);
    }

    // ── Album aggregation ──

    private static Album MakeAlbum(params Track[] tracks) => new() { Tracks = new(tracks) };

    [Fact]
    public void Album_HiResBeatsLosslessAndLossy()
    {
        var album = MakeAlbum(
            MakeTrack(codec: "MPEG-4 Audio (mp4a)", filePath: "a.m4a", bitrate: 256),
            MakeTrack(codec: "FLAC", filePath: "b.flac", sampleRate: 96000, bitsPerSample: 24),
            MakeTrack(codec: "FLAC", filePath: "c.flac", sampleRate: 44100, bitsPerSample: 16));
        Assert.Equal("Hi-Res Lossless", album.AudioQualityBadge);
    }

    [Fact]
    public void Album_LosslessBeatsLossy()
    {
        var album = MakeAlbum(
            MakeTrack(codec: "MPEG Version 1 Audio, Layer 3", filePath: "a.mp3", bitrate: 320),
            MakeTrack(codec: "FLAC", filePath: "b.flac", sampleRate: 44100, bitsPerSample: 16));
        Assert.Equal("Lossless", album.AudioQualityBadge);
    }

    [Fact]
    public void Album_LossyOnly_HighestBitrateCodecWins()
    {
        var album = MakeAlbum(
            MakeTrack(codec: "MPEG Version 1 Audio, Layer 3", filePath: "a.mp3", bitrate: 128),
            MakeTrack(codec: "MPEG-4 Audio (mp4a)", filePath: "b.m4a", bitrate: 256));
        Assert.Equal("AAC", album.AudioQualityBadge);
    }

    [Fact]
    public void Album_LossyOnly_DetailedInfoIncludesBitrate()
    {
        var album = MakeAlbum(
            MakeTrack(codec: "MPEG-4 Audio (mp4a)", filePath: "a.m4a", bitrate: 256, sampleRate: 44100));
        Assert.Contains("256 kbps", album.AudioQualityDetailedInfo);
        Assert.Contains("AAC", album.AudioQualityDetailedInfo);
    }

    [Fact]
    public void Album_Empty_ShowsNoBadge()
    {
        Assert.Equal(string.Empty, MakeAlbum().AudioQualityBadge);
        Assert.Equal(string.Empty, MakeAlbum().AudioQualityDescription);
    }

    [Fact]
    public void Album_Description_MatchesRepresentativeKind()
    {
        var lossy = MakeAlbum(MakeTrack(filePath: "a.mp3", bitrate: 320));
        var lossless = MakeAlbum(MakeTrack(codec: "FLAC", filePath: "b.flac"));
        Assert.Contains("Compressed", lossy.AudioQualityDescription);
        Assert.Contains("Lossless", lossless.AudioQualityDescription);
    }
}
