using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// A "nan dB" ReplayGain tag parsed as NaN. The library row drops it (Track.ApplyReplayGain),
/// but the player read the same value, and Math.Clamp(NaN, …) is NaN: the track's gain became
/// NaN instead of being bypassed like a missing tag.
/// </summary>
public class ReplayGainNonFiniteTagTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "rgnan-" + Guid.NewGuid().ToString("N"));

    public ReplayGainNonFiniteTagTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Theory]
    [InlineData("nan dB")]
    [InlineData("NaN")]
    [InlineData("inf dB")]
    [InlineData("-Infinity dB")]
    public void NonFiniteGain_IsNoTag(string value)
    {
        Assert.Null(ReplayGainTags.ParseDb(value));
    }

    [Fact]
    public void Player_ReadsANanGainTagAsNone()
    {
        var path = EmbeddedLyricsTagTests.Copy(_dir, "flac", f =>
        {
            var xiph = (TagLib.Ogg.XiphComment)f.GetTag(TagLib.TagTypes.Xiph, true);
            xiph.SetField("REPLAYGAIN_TRACK_GAIN", "nan dB");
            xiph.SetField("REPLAYGAIN_ALBUM_GAIN", "-6.00 dB");
        });

        var (track, album) = VlcAudioPlayer.ReadReplayGainTags(path);

        Assert.Null(track);
        Assert.Equal(-6.0, album);
    }
}
