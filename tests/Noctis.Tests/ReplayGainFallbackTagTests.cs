using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Files with no REPLAYGAIN_* tags but another loudness tag read as "no gain": Opus files
/// tagged only with R128_TRACK_GAIN / R128_ALBUM_GAIN (opusgain, ffmpeg, foobar2000's Opus
/// output) and iTunes files with only Sound Check's iTunNORM. Both are read now, only where the
/// ReplayGain tag itself is absent.
/// </summary>
public class ReplayGainFallbackTagTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public ReplayGainFallbackTagTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Copy(string ext)
    {
        var path = Path.Combine(_dir, "rg." + ext);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Audio", "Silence", "silence." + ext), path, true);
        return path;
    }

    private string OpusWith(params (string Key, string Value)[] fields)
    {
        var path = Copy("opus");
        using var f = TagLib.File.Create(path);
        var xiph = (TagLib.Ogg.XiphComment)f.GetTag(TagLib.TagTypes.Xiph, true);
        foreach (var (key, value) in fields) xiph.SetField(key, value);
        f.Save();
        return path;
    }

    // Sound Check of a loud song: 2000 (left) / 1800 (right) against iTunes' 1000 reference is
    // -3.01 dB; the 2500-based pair says the same. Peaks 0x7FFF; the rest as iTunes writes them.
    private const string LoudNorm = " 000007D0 00000708 00001388 00001194 00024CA8 00024CA8 00007FFF 00007FFF 00024CA8 00024CA8";

    [Fact]
    public void Opus_with_only_R128_tags_reads_them_as_ReplayGain()
    {
        // -2560 / 256 = -10 dB against -23 LUFS, ReplayGain's reference is 5 dB louder: -5 dB.
        var path = OpusWith(("R128_TRACK_GAIN", "-2560"), ("R128_ALBUM_GAIN", "512"));

        var rg = ReplayGainTags.ReadFile(path)!.Value;

        Assert.Equal(-5.0, rg.TrackGainDb!.Value, 3);
        Assert.Equal(7.0, rg.AlbumGainDb!.Value, 3);
    }

    [Fact]
    public void Opus_ReplayGain_tags_win_over_R128()
    {
        var path = OpusWith(("REPLAYGAIN_TRACK_GAIN", "-7.84 dB"), ("R128_TRACK_GAIN", "-2560"), ("R128_ALBUM_GAIN", "-1280"));

        var rg = ReplayGainTags.ReadFile(path)!.Value;

        Assert.Equal(-7.84, rg.TrackGainDb);
        Assert.Equal(0.0, rg.AlbumGainDb!.Value, 3);   // no REPLAYGAIN_ALBUM_GAIN: R128's album
    }

    [Fact]
    public void Opus_with_a_junk_R128_value_has_no_gain()
    {
        var path = OpusWith(("R128_TRACK_GAIN", "loud"));

        Assert.Null(ReplayGainTags.ReadFile(path)!.Value.TrackGainDb);
    }

    [Fact]
    public void Mp3_with_only_iTunNORM_reads_a_track_gain()
    {
        var path = Copy("mp3");
        using (var f = TagLib.File.Create(path))
        {
            var id3 = (TagLib.Id3v2.Tag)f.GetTag(TagLib.TagTypes.Id3v2, true);
            var comm = TagLib.Id3v2.CommentsFrame.Get(id3, "iTunNORM", "eng", true);
            comm.Text = LoudNorm;
            f.Save();
        }

        var rg = ReplayGainTags.ReadFile(path)!.Value;

        Assert.Equal(-3.01, rg.TrackGainDb!.Value, 2);
        Assert.Null(rg.AlbumGainDb);
    }

    [Fact]
    public void M4a_with_only_iTunNORM_reads_a_track_gain_and_ReplayGain_wins_when_present()
    {
        var path = Copy("m4a");
        using (var f = TagLib.File.Create(path))
        {
            var apple = (TagLib.Mpeg4.AppleTag)f.GetTag(TagLib.TagTypes.Apple, true);
            apple.SetDashBox("com.apple.iTunes", "iTunNORM", LoudNorm);
            f.Save();
        }
        Assert.Equal(-3.01, ReplayGainTags.ReadFile(path)!.Value.TrackGainDb!.Value, 2);

        using (var f = TagLib.File.Create(path))
        {
            var apple = (TagLib.Mpeg4.AppleTag)f.GetTag(TagLib.TagTypes.Apple, true);
            apple.SetDashBox("com.apple.iTunes", "REPLAYGAIN_TRACK_GAIN", "-6.50 dB");
            f.Save();
        }
        Assert.Equal(-6.50, ReplayGainTags.ReadFile(path)!.Value.TrackGainDb);
    }

    [Theory]
    // The more attenuating of the two pairs wins (the 2500-based pair is louder here).
    [InlineData(" 000003E8 000003E8 00001388 00001388 0 0 0 0 0 0", -3.01)]
    // A quiet song: 500 against 1000 is +3.01 dB of boost.
    [InlineData(" 000001F4 000001F4 000004E2 000004E2 0 0 0 0 0 0", 3.01)]
    public void ITunNorm_gain_takes_the_more_conservative_pair(string norm, double expected)
    {
        Assert.Equal(expected, ReplayGainTags.ParseITunNorm(norm)!.Value, 2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not hex at all")]
    [InlineData(" 00000000 00000000 00000000 00000000 0 0 0 0 0 0")]   // silence: no gain to take
    [InlineData(" 000003E8")]                                          // too short
    public void ITunNorm_that_cannot_be_read_gives_no_gain(string? norm)
    {
        Assert.Null(ReplayGainTags.ParseITunNorm(norm));
    }

    [Fact]
    public void ITunNorm_boost_is_capped()
    {
        // 1 against 1000 would be +30 dB: a near-silent track must not be blown up.
        var gain = ReplayGainTags.ParseITunNorm(" 00000001 00000001 00000001 00000001 0 0 0 0 0 0")!.Value;
        Assert.Equal(ReplayGainTags.MaxITunNormBoostDb, gain);
    }
}
