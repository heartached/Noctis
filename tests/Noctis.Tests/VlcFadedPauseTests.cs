using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #77: with "Fade on play and pause" on Windows, the fade rides the OS
/// session level, but NAudio's WasapiOut.Pause() leaves the ~100 ms already queued
/// rendering. Restoring the level straight after the pause played that tail back
/// at full volume — a short burst at the end of every faded pause. The restore
/// must wait out the buffer.
/// </summary>
public class VlcFadedPauseTests
{
    private static List<string> Run(bool fade, int drainMs)
    {
        var log = new List<string>();
        VlcAudioPlayer.RunFadedPause(
            fade, drainMs,
            () => log.Add("fadeOut"),
            () => log.Add("pause"),
            () => log.Add("restore"),
            ms => log.Add($"sleep:{ms}"));
        return log;
    }

    [Fact]
    public void RunFadedPause_RestoresOnlyAfterTheOutputPauseAndTheDrain()
    {
        var drain = VlcAudioPlayer.PausedOutputDrainMs(TimeSpan.FromMilliseconds(GaplessSink.OutputLatencyMs));

        Assert.Equal(new[] { "fadeOut", "pause", $"sleep:{drain}", "restore" }, Run(fade: true, drain));
    }

    [Fact]
    public void PausedOutputDrainMs_CoversTheEngineBufferWithMargin()
    {
        var drain = VlcAudioPlayer.PausedOutputDrainMs(TimeSpan.FromMilliseconds(GaplessSink.OutputLatencyMs));

        Assert.True(drain > GaplessSink.OutputLatencyMs, $"drain {drain} ms must exceed the {GaplessSink.OutputLatencyMs} ms buffer");
    }

    [Fact]
    public void RunFadedPause_WithoutFade_NeitherSleepsNorTouchesTheLevel()
    {
        Assert.Equal(new[] { "pause" }, Run(fade: false, drainMs: 150));
    }

    [Fact]
    public void RunFadedPause_NoBufferedOutput_RestoresImmediately()
    {
        // macOS/Linux and the legacy VLC path report no output latency: unchanged.
        Assert.Equal(0, VlcAudioPlayer.PausedOutputDrainMs(TimeSpan.Zero));
        Assert.Equal(new[] { "fadeOut", "pause", "restore" }, Run(fade: true, drainMs: 0));
    }
}
