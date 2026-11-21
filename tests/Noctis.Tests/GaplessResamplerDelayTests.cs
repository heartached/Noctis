using System.Linq;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

// VLC 3 runs every input through the resampler module it is told to use, even at equal rates.
// With speex (--speex-resampler-quality=10) an input comes out half a filter late and the half
// still in the filter at its end is never drained: the last 2.7-2.9 ms of every track were lost
// at a gapless seam (harness 11-19). The engine's players now run VLC's pass-through resampler
// and convert themselves; speex remains only for inputs above what amem takes (AmemRate), and
// its delay is compensated there: trimmed from the head, given back as silence at the end.
public class GaplessResamplerDelayTests
{
    [Theory]
    [InlineData(44100, 48000, 139)]   // up: 128 input frames = 139.3 at 48 kHz
    [InlineData(48000, 48000, 128)]   // 1:1 still runs the 256-tap filter
    [InlineData(44100, 44100, 128)]
    [InlineData(88200, 48000, 128)]   // down: 472-tap filter, 236 input frames = 128.4
    [InlineData(96000, 48000, 128)]   // down 2:1: 512 taps
    [InlineData(192000, 48000, 128)]
    [InlineData(352800, 176400, 128)] // what VLC still converts: a 352.8 kHz file halved for amem
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

    [Theory]
    [InlineData("using audio resampler module \"speex_resampler\"", "speex_resampler")]
    [InlineData("using audio resampler module \"soxr\"", "soxr")]
    [InlineData("using audio resampler module \"ugly\"", "ugly")]
    [InlineData("looking for audio resampler module matching \"speex\": 2 candidates", null)]
    [InlineData("using audio resampler module \"", null)]
    [InlineData(null, null)]
    public void ResamplerModule_FromLibVlcLogLine(string? line, string? expected) =>
        Assert.Equal(expected, VlcResamplerDelay.ModuleFromLogLine(line));

    [Fact]
    public void DelayedInput_HeadTrimmedAndTailPadded_KeepsItsPlaceOnTheTimeline()
    {
        // VLC's speex delays the input by 3 frames (2 near-silent ones here) and keeps its last 3.
        // Trimmed and padded, A plays its 7 frames that reached the engine and then 3 of silence
        // where its held tail was: B starts on frame 10, as without the delay.
        var provider = new GaplessSpliceProvider(8000, 1);
        var a = new GaplessTrackSegment(8000, 1, source: 0) { LeadTrimFrames = 3, TailPadFrames = 3 };
        var b = new GaplessTrackSegment(8000, 1, source: 1);
        provider.Enqueue(a);
        provider.Enqueue(b);
        Assert.True(a.Write(Enumerable.Repeat((short)0, 3).Concat(Enumerable.Repeat((short)16384, 7)).ToArray()));
        a.MarkEndOfStream();
        Assert.True(b.Write(Enumerable.Repeat((short)-16384, 20).ToArray()));
        b.MarkEndOfStream();
        var s = new float[20];
        provider.Read(s, 0, 20);
        Assert.All(s[..7], v => Assert.Equal(0.5f, v));
        Assert.All(s[7..10], v => Assert.Equal(0f, v));
        Assert.All(s[10..], v => Assert.Equal(-0.5f, v));
    }

    [Fact]
    public void TailPad_NotAddedToACutInput()
    {
        var seg = new GaplessTrackSegment(8000, 1, source: 0) { TailPadFrames = 5 };
        Assert.True(seg.Write(Enumerable.Repeat((short)100, 4).ToArray()));
        seg.Abandon();
        seg.MarkEndOfStream();
        Assert.Equal(0, seg.BufferedSamples);
    }
}
