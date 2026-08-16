using System;
using NAudio.Wave;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Regression tests for the exclusive-mode pause static (Discord report, veil,
/// 2026-08-16): pausing in WASAPI exclusive mode played constant static because
/// NAudio's <c>WasapiOut.Pause()</c> only stops FILLING the stream — the audio
/// client keeps running, and in exclusive event-driven mode the hardware then
/// loops the stale client-owned DMA buffer forever. Resume re-engaged the fill
/// loop out of phase over stale data: stutters/crackles.
///
/// The fix parks playback inside the render chain instead: the gain provider
/// ramps to zero click-free, then emits full buffers of true silence WITHOUT
/// consuming the buffered source, so the stream is never starved and resume
/// continues at the exact held sample. These tests drive the provider directly
/// (pure DSP — no audio device needed).
/// </summary>
public class WasapiGainPauseParkTests
{
    private const int Rate = 44100;
    private const int Channels = 2;

    /// <summary>Constant-amplitude source that counts every sample it serves.</summary>
    private sealed class CountingSource : ISampleProvider
    {
        public const float Amplitude = 0.5f;
        public long Served;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, Channels);

        public int Read(float[] buffer, int offset, int count)
        {
            for (var i = 0; i < count; i++) buffer[offset + i] = Amplitude;
            Served += count;
            return count;
        }
    }

    private static float[] GarbageBuffer(int samples)
    {
        var buffer = new float[samples];
        for (var i = 0; i < samples; i++) buffer[i] = 123f;
        return buffer;
    }

    [Fact]
    public void Park_RampsToSilence_ThenHoldsSourceAndEmitsFullSilentBuffers()
    {
        if (!OperatingSystem.IsWindows())
            return; // provider lives in the Windows-only sink

        var src = new CountingSource();
        var gain = new WasapiGainOutput.GainSampleProvider(src, Channels, Rate);

        // Steady state: pass-through at unity gain.
        var buffer = GarbageBuffer(Rate / 10); // 50ms
        var read = gain.Read(buffer, 0, buffer.Length);
        Assert.Equal(buffer.Length, read);
        Assert.All(buffer, s => Assert.Equal(CountingSource.Amplitude, s));

        // Park: the same 50ms read must fade out and END at exact digital zero
        // (the ramp is ~15ms), never stepping back up.
        gain.Park();
        buffer = GarbageBuffer(Rate / 10);
        read = gain.Read(buffer, 0, buffer.Length);
        Assert.Equal(buffer.Length, read);
        var previous = float.MaxValue;
        for (var i = 0; i < buffer.Length; i += Channels)
        {
            Assert.True(buffer[i] <= previous + 1e-6f, $"fade-out stepped up at frame {i / Channels}");
            previous = buffer[i];
        }
        Assert.Equal(0f, buffer[^1]);
        Assert.Equal(0f, buffer[^2]);

        // Fully parked: silence must be emitted as FULL buffers (a starved or
        // paused exclusive stream is what looped the stale DMA buffer as static),
        // stale buffer content must be overwritten, and the source must NOT be
        // consumed — the pre-pause tail is held for a sample-exact resume.
        var servedWhenParked = src.Served;
        for (var pass = 0; pass < 10; pass++)
        {
            buffer = GarbageBuffer(Rate / 10);
            read = gain.Read(buffer, 0, buffer.Length);
            Assert.Equal(buffer.Length, read);
            Assert.All(buffer, s => Assert.Equal(0f, s));
        }
        Assert.Equal(servedWhenParked, src.Served);
    }

    [Fact]
    public void Unpark_RampsBackUp_AndResumesConsumingTheHeldSource()
    {
        if (!OperatingSystem.IsWindows())
            return; // provider lives in the Windows-only sink

        var src = new CountingSource();
        var gain = new WasapiGainOutput.GainSampleProvider(src, Channels, Rate);

        var buffer = new float[Rate / 10]; // 50ms
        gain.Read(buffer, 0, buffer.Length);

        gain.Park();
        gain.Read(buffer, 0, buffer.Length); // fade-out completes inside this read
        gain.Read(buffer, 0, buffer.Length); // fully parked, source held
        var servedWhileParked = src.Served;

        gain.Unpark();
        buffer = GarbageBuffer(Rate / 10);
        var read = gain.Read(buffer, 0, buffer.Length);
        Assert.Equal(buffer.Length, read);

        // Consumption resumes immediately (no skip while parked)...
        Assert.Equal(servedWhileParked + buffer.Length, src.Served);

        // ...ramping up click-free from silence back to unity pass-through.
        Assert.True(buffer[0] < CountingSource.Amplitude / 2,
            $"resume started at {buffer[0]} — expected a fade-in from near-silence");
        var previous = -1f;
        for (var i = 0; i < buffer.Length; i += Channels)
        {
            Assert.True(buffer[i] >= previous - 1e-6f, $"fade-in stepped down at frame {i / Channels}");
            previous = buffer[i];
        }
        Assert.Equal(CountingSource.Amplitude, buffer[^1]);
        Assert.Equal(CountingSource.Amplitude, buffer[^2]);
    }

    [Fact]
    public void Park_FadeOut_ConsumesOnlyTheRampWorthOfSource()
    {
        if (!OperatingSystem.IsWindows())
            return; // provider lives in the Windows-only sink

        var src = new CountingSource();
        var gain = new WasapiGainOutput.GainSampleProvider(src, Channels, Rate);

        var buffer = new float[Rate / 10];
        gain.Read(buffer, 0, buffer.Length);
        var servedBeforePark = src.Served;

        // The fade-out needs only ~15ms of source; the rest of the pre-pause
        // tail must stay in the buffer for resume, not be eaten at zero gain.
        gain.Park();
        gain.Read(buffer, 0, buffer.Length);
        var consumedByFade = src.Served - servedBeforePark;
        Assert.True(consumedByFade < Rate / 20, // well under 25ms' worth (samples, stereo ≈ 11ms)
            $"fade-out consumed {consumedByFade} samples — the held tail is being discarded");
    }
}
