using System;
using Noctis.Services;

namespace Noctis.Helpers;

/// <summary>
/// What the flowing-artwork background needs from playback, sampled once per frame.
/// </summary>
public readonly record struct BeatContext(int Bpm, double PositionMs, bool IsPlaying);

/// <summary>
/// Picks the beat pulse for a frame: the live <see cref="BeatMeter"/> when samples are
/// flowing through our own output chain (the Windows engines), else a metronome on the
/// track's tagged/analysed BPM phased from the playback position, else nothing.
/// The BPM grid has no downbeat information, so it keeps time but may sit off the
/// true beat — good enough to move with the tempo where the meter has no audio.
/// </summary>
public static class BeatPulseSource
{
    public static double Evaluate(BeatMeter meter, double nowMs, BeatContext ctx)
    {
        if (meter.TryRead(nowMs, out var live))
            return Math.Clamp(live, 0, 1);
        return FromBpmGrid(ctx);
    }

    /// <summary>Pure metronome pulse: 1 on each beat, decaying with the meter's curve.</summary>
    public static double FromBpmGrid(BeatContext ctx)
    {
        if (!ctx.IsPlaying || ctx.Bpm <= 0 || ctx.PositionMs < 0 || double.IsNaN(ctx.PositionMs))
            return 0;
        var periodMs = 60000.0 / ctx.Bpm;
        var phaseMs = ctx.PositionMs % periodMs;
        return Math.Exp(-phaseMs / BeatMeter.DecayMs);
    }
}
