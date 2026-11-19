using System.Linq;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

// VLC 3 runs every input through its speex resampler (even 48 kHz into a 48 kHz engine),
// which delays the input by half its filter and never drains that half at the end. A
// gapless seam therefore swaps the outgoing track's held-back tail for the incoming one's
// delay; between two different delays the timeline stepped (harness 11-19: -0.23 ms into
// a 48/88.2/96 kHz or DSD track after a 44.1 kHz one, +0.23 ms back out).
public class GaplessResamplerDelayTests
{
    [Theory]
    [InlineData(44100, 48000, 139)]   // up: 128 input frames = 139.3 at 48 kHz
    [InlineData(48000, 48000, 128)]   // 1:1 still runs the 256-tap filter
    [InlineData(44100, 44100, 128)]
    [InlineData(88200, 48000, 128)]   // down: 472-tap filter, 236 input frames = 128.4
    [InlineData(96000, 48000, 128)]   // down 2:1: 512 taps
    [InlineData(192000, 48000, 128)]
    [InlineData(22050, 48000, 279)]
    [InlineData(48000, 44100, 129)]   // down 160:147: 280 taps, 140 input frames = 128.6, rounded as speex does
    public void SpeexDelay_MatchesSpeexOutputLatency(int inRate, int outRate, int expected) =>
        Assert.Equal(expected, VlcResamplerDelay.OutputFrames(inRate, outRate));

    [Theory]
    [InlineData(0, 48000)]
    [InlineData(44100, 0)]
    [InlineData(-1, 48000)]
    public void UnknownRate_HasNoDelay(int inRate, int outRate) =>
        Assert.Equal(0, VlcResamplerDelay.OutputFrames(inRate, outRate));

    private static short[] Block(short value, int samples) => Enumerable.Repeat(value, samples).ToArray();

    // A (16384) ends naturally, B (-16384) follows; returns the 60 frames around the seam.
    private static float[] Seam(int delayA, int delayB, bool abandonA = false)
    {
        var provider = new GaplessSpliceProvider(8000, 1);
        var a = new GaplessTrackSegment(8000, 1, source: 0) { ResamplerDelayFrames = delayA };
        var b = new GaplessTrackSegment(8000, 1, source: 1) { ResamplerDelayFrames = delayB };
        provider.Enqueue(a);
        provider.Enqueue(b);
        Assert.True(a.Write(Block(16384, 100)));
        Assert.True(b.Write(Block(-16384, 400)));
        provider.Read(new float[70], 0, 70);
        if (abandonA) a.Abandon(); else a.MarkEndOfStream();
        var seam = new float[60];
        provider.Read(seam, 0, 60);
        return seam;
    }

    [Fact]
    public void LongerDelayOut_ShorterIn_PadsTheDifference()
    {
        // A's lost tail (139) is longer than B's delay (128): 11 frames go missing unless padded.
        var s = Seam(139, 128);
        Assert.All(s[..30], v => Assert.Equal(0.5f, v));
        Assert.All(s[30..41], v => Assert.Equal(0f, v));
        Assert.All(s[41..], v => Assert.Equal(-0.5f, v));
    }

    [Fact]
    public void ShorterDelayOut_LongerIn_DropsTheDifference()
    {
        var provider = new GaplessSpliceProvider(8000, 1);
        var a = new GaplessTrackSegment(8000, 1, source: 0) { ResamplerDelayFrames = 128 };
        var b = new GaplessTrackSegment(8000, 1, source: 1) { ResamplerDelayFrames = 139 };
        provider.Enqueue(a);
        provider.Enqueue(b);
        Assert.True(a.Write(Block(16384, 100)));
        var head = Enumerable.Range(0, 400).Select(i => (short)(i < 11 ? 1000 : -16384)).ToArray();
        Assert.True(b.Write(head));
        provider.Read(new float[70], 0, 70);
        a.MarkEndOfStream();
        var s = new float[60];
        provider.Read(s, 0, 60);
        Assert.All(s[..30], v => Assert.Equal(0.5f, v));
        Assert.All(s[30..], v => Assert.Equal(-0.5f, v)); // B's first 11 frames are gone
    }

    [Theory]
    [InlineData(139, 139)]
    [InlineData(0, 128)]   // a delay not known on one side: left as it was
    [InlineData(139, 0)]
    public void EqualOrUnknownDelays_SpliceUntouched(int delayA, int delayB)
    {
        var s = Seam(delayA, delayB);
        Assert.All(s[..30], v => Assert.Equal(0.5f, v));
        Assert.All(s[30..], v => Assert.Equal(-0.5f, v));
    }

    [Fact]
    public void AnotherResamplerInUse_SpliceUntouched()
    {
        // A system LibVLC without speex: the speex delays describe nothing, so no alignment.
        var provider = new GaplessSpliceProvider(8000, 1) { ResamplerDelayApplies = () => false };
        var a = new GaplessTrackSegment(8000, 1, source: 0) { ResamplerDelayFrames = 139 };
        var b = new GaplessTrackSegment(8000, 1, source: 1) { ResamplerDelayFrames = 128 };
        provider.Enqueue(a);
        provider.Enqueue(b);
        Assert.True(a.Write(Block(16384, 100)));
        Assert.True(b.Write(Block(-16384, 400)));
        provider.Read(new float[70], 0, 70);
        a.MarkEndOfStream();
        var s = new float[60];
        provider.Read(s, 0, 60);
        Assert.All(s[..30], v => Assert.Equal(0.5f, v));
        Assert.All(s[30..], v => Assert.Equal(-0.5f, v));
    }

    [Theory]
    [InlineData("using audio resampler module \"speex_resampler\"", "speex_resampler")]
    [InlineData("using audio resampler module \"soxr\"", "soxr")]
    [InlineData("using audio resampler module \"ugly_resampler\"", "ugly_resampler")]
    [InlineData("looking for audio resampler module matching \"speex\": 2 candidates", null)]
    [InlineData("using audio resampler module \"", null)]
    [InlineData(null, null)]
    public void ResamplerModule_FromLibVlcLogLine(string? line, string? expected) =>
        Assert.Equal(expected, VlcResamplerDelay.ModuleFromLogLine(line));

    [Theory]
    [InlineData(true, "speex_resampler", false, true)]
    [InlineData(true, "soxr", true, false)]            // the plugin is missing: fell back
    [InlineData(true, "ugly_resampler", false, false)]
    [InlineData(true, "samplerate", false, false)]
    [InlineData(true, null, true, true)]               // nothing logged yet, bundled LibVLC (Windows)
    [InlineData(true, null, false, false)]             // nothing logged yet, system LibVLC (Linux)
    [InlineData(false, "speex_resampler", true, false)] // speex not forced: not quality 10
    public void Correction_OnlyWithSpeexReallyLoaded(bool forced, string? module, bool bundled, bool expected) =>
        Assert.Equal(expected, VlcResamplerDelay.CorrectionApplies(forced, module, bundled));

    [Fact]
    public void CutSeam_IsNotAligned()
    {
        // An abandoned (cut) outgoing track did not lose its tail to the end of its input.
        var s = Seam(139, 128, abandonA: true);
        Assert.All(s, v => Assert.Equal(-0.5f, v));
    }
}
