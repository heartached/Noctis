using System;
using NAudio.Wave;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Regression tests for A23: in exclusive mode (WasapiGainOutput) a seek/stop
/// flush or a dry queue stopped LIVE audio with a hard step — the queue's
/// zero-fill butt-joined to the last emitted frame, then post-cut audio starting
/// from silence at arbitrary amplitude — and exclusive mode has no OS mixer to
/// soften it. The gain stage now ramps the last emitted frame down and fades in
/// whatever follows. These tests drive the provider directly (pure DSP — no
/// audio device needed).
/// </summary>
public class WasapiGainCutDeclickTests
{
    private const int Rate = 44100;
    private const int Channels = 2;
    private const int TenMs = Rate / 100 * Channels;
    private const int FiveMs = Rate / 200 * Channels;

    // A ~5 ms linear ramp over a 0.5 swing steps ~0.002 per frame; a hard cut steps 0.5+.
    private const float MaxStep = 0.02f;

    /// <summary>
    /// Constant-level queue that short-reads when dry, like the sink's
    /// BufferedWaveProvider (ReadFully off) after an underrun or a flush.
    /// </summary>
    private sealed class QueueSource : ISampleProvider
    {
        private int _available;
        private float _level;
        public int Served;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, Channels);

        public void Enqueue(float level, int samples)
        {
            _level = level;
            _available += samples;
        }

        public void Clear() => _available = 0;

        public int Read(float[] buffer, int offset, int count)
        {
            var n = Math.Min(count, _available);
            for (var i = 0; i < n; i++) buffer[offset + i] = _level;
            _available -= n;
            Served += n;
            return n;
        }
    }

    private static float[] GarbageBuffer(int samples)
    {
        var buffer = new float[samples];
        for (var i = 0; i < samples; i++) buffer[i] = 123f;
        return buffer;
    }

    // Every frame (including the first, against the previous read's last frame)
    // must move by at most MaxStep on every channel.
    private static void AssertContinuous(float[] buffer, float[] prev, string what)
    {
        for (var i = 0; i < buffer.Length; i += Channels)
        {
            for (var c = 0; c < Channels; c++)
            {
                var step = Math.Abs(buffer[i + c] - prev[c]);
                Assert.True(step <= MaxStep,
                    $"{what}: step of {step:F4} at frame {i / Channels} ch {c} ({prev[c]:F4} -> {buffer[i + c]:F4})");
                prev[c] = buffer[i + c];
            }
        }
    }

    [Fact]
    public void Underrun_RampsTheTailDown_ThenFadesTheRefillIn()
    {
        if (!OperatingSystem.IsWindows())
            return; // provider lives in the Windows-only sink

        var src = new QueueSource();
        var gain = new WasapiGainOutput.GainSampleProvider(src, Channels, Rate);

        // Steady state: bit-exact unity pass-through.
        src.Enqueue(0.5f, TenMs);
        var buffer = GarbageBuffer(TenMs);
        Assert.Equal(buffer.Length, gain.Read(buffer, 0, buffer.Length));
        Assert.All(buffer, s => Assert.Equal(0.5f, s));
        var prev = new[] { 0.5f, 0.5f };

        // The queue runs dry 5 ms into a 20 ms read: the rest must be the tail
        // ramped to silence, and the full count claimed (a short read stops
        // WasapiOut's render loop / starves the exclusive stream).
        src.Enqueue(0.5f, FiveMs);
        buffer = GarbageBuffer(2 * TenMs);
        Assert.Equal(buffer.Length, gain.Read(buffer, 0, buffer.Length));
        AssertContinuous(buffer, prev, "underrun edge");
        Assert.Equal(0f, buffer[^1]);
        Assert.Equal(0f, buffer[^2]);

        // Audio resumes from silence: faded in, then exact again.
        src.Enqueue(-0.5f, 4 * TenMs);
        buffer = GarbageBuffer(2 * TenMs);
        Assert.Equal(buffer.Length, gain.Read(buffer, 0, buffer.Length));
        AssertContinuous(buffer, prev, "refill after underrun");
        Assert.Equal(-0.5f, buffer[^1]);
        Assert.Equal(-0.5f, buffer[^2]);
    }

    [Fact]
    public void Cut_RampsThePreCutTail_ThenFadesInThePostCutAudio()
    {
        if (!OperatingSystem.IsWindows())
            return; // provider lives in the Windows-only sink

        var src = new QueueSource();
        var gain = new WasapiGainOutput.GainSampleProvider(src, Channels, Rate);

        src.Enqueue(0.5f, 10 * TenMs);
        var buffer = GarbageBuffer(TenMs);
        gain.Read(buffer, 0, buffer.Length);
        var prev = new[] { buffer[^2], buffer[^1] };
        Assert.Equal(0.5f, prev[0]);

        // Seek flush, and the refill lands before the next render read (the
        // common case: exclusive reads ~100ms at a time) — no silence between,
        // so only the cut itself can arm the declick.
        var cleared = false;
        gain.Cut(() => { src.Clear(); cleared = true; });
        Assert.True(cleared);
        src.Enqueue(-0.5f, 10 * TenMs);
        var servedBeforeRead = src.Served;

        buffer = GarbageBuffer(2 * TenMs);
        Assert.Equal(buffer.Length, gain.Read(buffer, 0, buffer.Length));
        AssertContinuous(buffer, prev, "cut junction");
        // The tail ramp is synthesized from the last emitted frame: it eats none
        // of the post-cut audio, and no pre-cut audio survives the clear.
        Assert.Equal(buffer.Length - FiveMs, src.Served - servedBeforeRead);
        for (var i = FiveMs; i < buffer.Length; i++)
            Assert.True(buffer[i] <= 0f, $"pre-cut audio at sample {i}: {buffer[i]}");
        Assert.Equal(-0.5f, buffer[^1]);
        Assert.Equal(-0.5f, buffer[^2]);
    }
}
