using System.Diagnostics;
using NAudio.Wave;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Silent test mode (NOCTIS_AOUT=dummy): the gapless engine renders into
/// <see cref="NullWavePlayer"/> instead of a real WasapiOut, so a runtime test can drive
/// the default Windows path without any audio device ever opening. The null sink must
/// still pull at real-time pace and honour the transport the way WasapiOut does, or the
/// engine under test would not behave as it ships.
/// </summary>
public class NullWavePlayerTests
{
    private const int Rate = 48000;
    private const int Channels = 2;

    /// <summary>Endless float source that counts the frames it serves.</summary>
    private sealed class CountingProvider : IWaveProvider
    {
        private long _bytes;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, Channels);
        public long Frames => Interlocked.Read(ref _bytes) / WaveFormat.BlockAlign;

        public int Read(byte[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            Interlocked.Add(ref _bytes, count);
            return count;
        }
    }

    /// <summary>A source that is already at its end: every read returns 0 bytes.</summary>
    private sealed class EmptyProvider : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, Channels);
        public int Read(byte[] buffer, int offset, int count) => 0;
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("mmdevice", false)]
    [InlineData("directsound", false)]
    [InlineData("dummy2", false)]
    [InlineData("dummy", true)]
    [InlineData("DUMMY", true)]
    [InlineData(" Dummy ", true)]
    public void IsSilentAudio_IsTrueOnlyForDummy(string? env, bool expected)
    {
        Assert.Equal(expected, NullWavePlayer.IsSilentAudio(env));
    }

    [Fact]
    public void Play_PullsFramesAtRealTimePace()
    {
        var source = new CountingProvider();
        using var player = new NullWavePlayer();
        player.Init(source);

        var clock = Stopwatch.StartNew();
        player.Play();
        Thread.Sleep(300);
        player.Stop();
        clock.Stop();

        // Measured, not the nominal 300 ms: Sleep and the Stop join can both run long.
        var expected = clock.Elapsed.TotalSeconds * Rate;
        Assert.InRange(source.Frames, (long)(expected * 0.65), (long)(expected * 1.35));
    }

    [Fact]
    public void Pause_StopsPulling_AndPlayResumes()
    {
        var source = new CountingProvider();
        using var player = new NullWavePlayer();
        player.Init(source);

        player.Play();
        Thread.Sleep(100);
        player.Pause();
        Assert.Equal(PlaybackState.Paused, player.PlaybackState);
        Thread.Sleep(50); // a pull already in flight finishes
        var atPause = source.Frames;
        Thread.Sleep(200);
        Assert.Equal(atPause, source.Frames);

        player.Play();
        Assert.Equal(PlaybackState.Playing, player.PlaybackState);
        Thread.Sleep(100);
        Assert.True(source.Frames > atPause, "Play after Pause must resume pulling");
    }

    [Fact]
    public void Stop_RaisesPlaybackStoppedWithoutException()
    {
        using var player = new NullWavePlayer();
        player.Init(new CountingProvider());
        StoppedEventArgs? stopped = null;
        using var raised = new ManualResetEventSlim();
        player.PlaybackStopped += (_, e) =>
        {
            stopped = e;
            raised.Set();
        };

        player.Play();
        Thread.Sleep(30);
        player.Stop();

        Assert.True(raised.Wait(2000, TestContext.Current.CancellationToken), "PlaybackStopped was not raised");
        Assert.Null(stopped!.Exception);
        Assert.Equal(PlaybackState.Stopped, player.PlaybackState);
    }

    [Fact]
    public void Dispose_EndsTheRenderThread()
    {
        var source = new CountingProvider();
        var player = new NullWavePlayer();
        player.Init(source);
        var stoppedCount = 0;
        player.PlaybackStopped += (_, _) => Interlocked.Increment(ref stoppedCount);

        player.Play();
        Thread.Sleep(50);
        player.Dispose();

        // Dispose joins the thread, so nothing may be pulled after it returns.
        var atDispose = source.Frames;
        Thread.Sleep(100);
        Assert.Equal(atDispose, source.Frames);
        Assert.Equal(1, Volatile.Read(ref stoppedCount));
        Assert.Equal(PlaybackState.Stopped, player.PlaybackState);
    }

    [Fact]
    public void ZeroByteRead_EndsPlayback_LikeWasapiOut()
    {
        using var player = new NullWavePlayer();
        player.Init(new EmptyProvider());
        using var raised = new ManualResetEventSlim();
        player.PlaybackStopped += (_, _) => raised.Set();

        player.Play();

        Assert.True(raised.Wait(2000, TestContext.Current.CancellationToken), "end of stream must stop the player");
        Assert.Equal(PlaybackState.Stopped, player.PlaybackState);
    }
}
