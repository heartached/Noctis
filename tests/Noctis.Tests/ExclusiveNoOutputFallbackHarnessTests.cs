using System.Diagnostics;
using System.Reflection;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Exclusive Mode with no usable output at all: neither an exclusive nor a shared WASAPI sink
/// opens, so PrepareExclusiveOutputFor turns Exclusive Mode off and rebuilds the players on
/// shared output mid-PlayInternal. That rebuild disposes the Media being opened; the track must
/// still start on the rebuilt output, at its start position, paused when asked, opened once.
/// NOCTIS_AOUT=dummy makes every WasapiGainOutput.TryCreate* return null, which is exactly
/// that machine state (the splice engine renders to a null output there).
/// </summary>
public class ExclusiveNoOutputFallbackHarnessTests : IDisposable
{
    private readonly ITestOutputHelper _o;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public ExclusiveNoOutputFallbackHarnessTests(ITestOutputHelper o)
    {
        _o = o;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Tone(int seconds)
    {
        const int rate = 44100;
        var pcm = new float[rate * seconds];
        for (var i = 0; i < pcm.Length; i++) pcm[i] = 0.3f * MathF.Sin(2 * MathF.PI * 440 * i / rate);
        var path = Path.Combine(_dir, "tone.wav");
        PluginAudioDecodeTests.WriteWav(path, pcm, rate);
        return path;
    }

    private static bool WaitFor(Func<bool> done, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (!done() && sw.ElapsedMilliseconds < timeoutMs) Thread.Sleep(20);
        return done();
    }

    private (VlcAudioPlayer Player, List<string> Log, List<string> Notices, List<string> Errors) ExclusivePlayer()
    {
        var log = new List<string>();
        DebugLogger.IsEnabled = true;
        DebugLogger.EntryAdded += en =>
        {
            if (en.Action.StartsWith("Exclusive.", StringComparison.Ordinal) || en.Action.StartsWith("VLC.Play", StringComparison.Ordinal))
                lock (log) log.Add($"{en.Action} | {en.Metadata}");
        };
        var player = new VlcAudioPlayer();
        var notices = new List<string>();
        var errors = new List<string>();
        player.OutputModeChanged += (_, m) => { lock (notices) notices.Add(m); };
        player.PlaybackError += (_, e) => { lock (errors) errors.Add(e); };
        player.SetExclusiveMode(true);
        Assert.True(WaitFor(() => { lock (log) return log.Exists(l => l.StartsWith("Exclusive.ModeSwitched | exclusive=True", StringComparison.Ordinal)); }, 10000),
            "never switched to exclusive");
        return (player, log, notices, errors);
    }

    private void Dump(List<string> log, List<string> notices, List<string> errors)
    {
        lock (log) foreach (var l in log) _o.WriteLine(l);
        lock (notices) foreach (var n in notices) _o.WriteLine("notice: " + n);
        lock (errors) foreach (var e in errors) _o.WriteLine("error: " + e);
    }

    [Fact]
    public void NoOutput_FallsBackToShared_AndTheSameTrackPlays_FromItsStartPosition()
    {
        Assert.SkipUnless(NullWavePlayer.SilentMode, "needs NOCTIS_AOUT=dummy for the test run");
        var path = Tone(20);
        var (player, log, notices, errors) = ExclusivePlayer();
        using (player)
        {
            long heard = 0;
            player.PositionChanged += (_, p) => Interlocked.Exchange(ref heard, (long)p.TotalMilliseconds);
            player.PendingSeekMs = 5000;
            player.Play(path);

            var moved = WaitFor(() => Interlocked.Read(ref heard) >= 6000, 10000);
            Dump(log, notices, errors);
            lock (notices) Assert.Contains(notices, n => n.Contains("Exclusive Mode was turned off", StringComparison.Ordinal));
            Assert.True(player.ExclusiveModePaused);
            lock (errors) Assert.Empty(errors);
            Assert.True(moved, $"the track never played after the fallback (heard={Interlocked.Read(ref heard)})");
            Assert.InRange(Interlocked.Read(ref heard), 5000, 12000); // started at 5 s, not 0:00
            Assert.Equal(PlaybackState.Playing, player.State);
            lock (log)
            {
                Assert.Single(log, l => l.StartsWith("Exclusive.SetupFailed", StringComparison.Ordinal)); // tried once, no loop
                Assert.Single(log, l => l.StartsWith("VLC.Play.Reopen", StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public void NoOutput_FallsBackToShared_AndAPausedStartStaysPaused_ThenResumes()
    {
        Assert.SkipUnless(NullWavePlayer.SilentMode, "needs NOCTIS_AOUT=dummy for the test run");
        var path = Tone(20);
        var (player, log, notices, errors) = ExclusivePlayer();
        using (player)
        {
            long heard = 0;
            player.PositionChanged += (_, p) => Interlocked.Exchange(ref heard, (long)p.TotalMilliseconds);
            // The restart-paused request (drag-to-start / Previous while paused).
            typeof(VlcAudioPlayer).GetField("_restartPausedRequest", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(player, true);
            player.PendingSeekMs = 4000;
            player.Play(path);

            Assert.True(WaitFor(() => { lock (notices) return notices.Exists(n => n.Contains("Exclusive Mode was turned off", StringComparison.Ordinal)); }, 10000));
            Thread.Sleep(1500);
            Dump(log, notices, errors);
            lock (errors) Assert.Empty(errors);
            Assert.Equal(PlaybackState.Paused, player.State);
            var parked = Interlocked.Read(ref heard);

            player.Resume();
            Assert.True(WaitFor(() => Interlocked.Read(ref heard) >= 5000, 10000),
                $"never played after Resume (parked at {parked}, heard={Interlocked.Read(ref heard)})");
            Assert.InRange(Interlocked.Read(ref heard), 4000, 12000);
            Assert.Equal(PlaybackState.Playing, player.State);
        }
    }
}
