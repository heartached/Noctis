using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The native (Linux/macOS) volume path parks MediaPlayer.Volume at 0 at every
/// track start and before every in-place seek, and the fade-in is the ONLY thing
/// that un-parks it — there is no periodic re-assert on that path
/// (ScheduleSessionVolumeReassert returns immediately when _sessionVolume is null).
/// So any run of the fade that ends below its target leaves the track quiet, or
/// silent, for its whole duration until the user forces a new track. That is the
/// "plays but no audio, skip forward then back and it works" report.
/// </summary>
public class VlcVolumeFadeTests
{
    private sealed class Recorder
    {
        public readonly List<int> Steps = new();
        public readonly List<int> Landings = new();
        public int Sleeps;

        /// <summary>Last value actually written to the player, in call order.</summary>
        public int? Final { get; private set; }

        public void Step(int v) { Steps.Add(v); Final = v; }
        public void Land(int v) { Landings.Add(v); Final = v; }
        public void Sleep() => Sleeps++;

        /// <summary>A guarded write that the reentrancy guard dropped — writes nothing.</summary>
        public void DroppedStep(int v) => Steps.Add(v);
    }

    private const int StepMs = 4;

    [Fact]
    public void RunVolumeFadeIn_LandsOnTargetAfterAFullRun()
    {
        var r = new Recorder();

        VlcAudioPlayer.RunVolumeFadeIn(80, 40, StepMs, () => true, r.Step, r.Land, r.Sleep);

        Assert.Equal(80, r.Final);
    }

    [Fact]
    public void RunVolumeFadeIn_LandsOnTargetWhenTheTrackSwapsMidFade()
    {
        // A seek worker racing a track change: the fade bails on the media check.
        // Returning without landing is what strands the player at a partial volume.
        var r = new Recorder();
        var calls = 0;

        VlcAudioPlayer.RunVolumeFadeIn(80, 40, StepMs, () => ++calls <= 3, r.Step, r.Land, r.Sleep);

        Assert.Equal(80, r.Final);
    }

    [Fact]
    public void RunVolumeFadeIn_LandsOnTargetWhenItStopsBeforeTheFirstStep()
    {
        // The worst case: the player is still parked at 0 from the caller's park
        // write, so bailing without landing means a completely silent track.
        var r = new Recorder();

        VlcAudioPlayer.RunVolumeFadeIn(80, 40, StepMs, () => false, r.Step, r.Land, r.Sleep);

        Assert.Empty(r.Steps);
        Assert.Equal(80, r.Final);
    }

    [Fact]
    public void RunVolumeFadeIn_StepsRiseFromSilenceAndNeverExceedTarget()
    {
        var r = new Recorder();

        VlcAudioPlayer.RunVolumeFadeIn(80, 40, StepMs, () => true, r.Step, r.Land, r.Sleep);

        Assert.NotEmpty(r.Steps);
        Assert.All(r.Steps, v => Assert.InRange(v, 0, 80));
        for (var i = 1; i < r.Steps.Count; i++)
            Assert.True(r.Steps[i] >= r.Steps[i - 1], "fade must rise monotonically");
    }

    [Fact]
    public void RunVolumeFadeIn_LeavesTheFinalValueToTheLandingWriterNotTheDroppableOne()
    {
        // SetPlayerVolumeGuarded drops a write outright under contention. If the
        // step that carries the target is a guarded one, losing it strands the
        // player below target with nothing to correct it.
        var r = new Recorder();

        VlcAudioPlayer.RunVolumeFadeIn(80, 40, StepMs, () => true, r.Step, r.Land, r.Sleep);

        Assert.DoesNotContain(80, r.Steps);
        Assert.Equal(new[] { 80 }, r.Landings);
    }

    [Fact]
    public void RunVolumeFadeIn_RepeatedRunsDoNotRatchetTheVolumeDown()
    {
        // Scrubbing applies one fade per coalesced seek. While the post-seek restore
        // value was read live off MediaPlayer.Volume, every run whose last guarded
        // write was dropped became the next run's ceiling: 80 → 64 → 51 → … → silence.
        var target = 80;
        var current = target;

        for (var seek = 0; seek < 20; seek++)
        {
            var r = new Recorder();
            // Guarded step writes all dropped; only the landing write lands.
            VlcAudioPlayer.RunVolumeFadeIn(current, 20, StepMs, () => true, r.DroppedStep, r.Land, r.Sleep);
            current = r.Final ?? 0;
        }

        Assert.Equal(target, current);
    }
}
