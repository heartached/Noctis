using System.Linq;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

// A21: the engine end-watchdog judged "drained" by whatever segment the sink was
// rendering. After a gapless splice into a short track that had fully decoded
// while staged (its EndReached ignored as inactive), the active segment is still
// the outgoing tail from the other slot — the watchdog armed the end grace at
// once and TrackEnded skipped the short track. The drain test now follows the
// current player's own segment.
public class GaplessEndWatchdogTests
{
    private static short[] Block(int samples) => Enumerable.Repeat((short)8192, samples).ToArray();

    [Fact]
    public void ShortStagedTrack_IsNotDrained_UntilItsOwnSegmentPlaysOut()
    {
        var provider = new GaplessSpliceProvider(8000, 1);
        var outgoing = new GaplessTrackSegment(8000, 1, source: 0);
        var shortTrack = new GaplessTrackSegment(8000, 1, source: 1);
        provider.Enqueue(outgoing);
        provider.Enqueue(shortTrack);
        Assert.True(outgoing.Write(Block(4000)));    // 0.5 s tail still to render
        outgoing.MarkEndOfStream();
        Assert.True(shortTrack.Write(Block(16000))); // the whole 2 s track, decoded while staged
        shortTrack.MarkEndOfStream();

        // Splice done, tail rendering: the sink is still on the outgoing slot, which
        // the old test read as "drained" for the new player (slot 1).
        provider.Read(new float[800], 0, 800);
        Assert.Same(outgoing, provider.ActiveSegment);
        Assert.False(VlcAudioPlayer.EngineEndedInputDrained(shortTrack));

        // Tail done, the short track is audible but not played out yet.
        provider.Read(new float[4000], 0, 4000);
        Assert.Same(shortTrack, provider.ActiveSegment);
        Assert.False(VlcAudioPlayer.EngineEndedInputDrained(shortTrack));

        provider.Read(new float[15200], 0, 15200);
        Assert.True(VlcAudioPlayer.EngineEndedInputDrained(shortTrack));
    }

    [Fact]
    public void MissedEndOfStream_IsMarked_AndDrainsOncePlayedOut()
    {
        // The wedge the watchdog exists for: VLC says Ended but no EOS reached the
        // segment. The drain test marks it, so it finishes once the ring empties.
        var provider = new GaplessSpliceProvider(8000, 1);
        var seg = new GaplessTrackSegment(8000, 1, source: 0);
        provider.Enqueue(seg);
        Assert.True(seg.Write(Block(800)));

        Assert.False(VlcAudioPlayer.EngineEndedInputDrained(seg));
        Assert.True(seg.EndOfStream);

        provider.Read(new float[800], 0, 800);
        Assert.True(VlcAudioPlayer.EngineEndedInputDrained(seg));
    }

    [Fact]
    public void NoSegmentOrAbandonedSegment_CountsAsDrained()
    {
        Assert.True(VlcAudioPlayer.EngineEndedInputDrained(null));

        var seg = new GaplessTrackSegment(8000, 1, source: 1);
        Assert.True(seg.Write(Block(800)));
        seg.Abandon(); // a manual skip / stop cleared it
        Assert.True(VlcAudioPlayer.EngineEndedInputDrained(seg));
    }
}
