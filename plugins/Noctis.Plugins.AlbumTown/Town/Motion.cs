namespace Noctis.Plugins.AlbumTown.Town;

/// <summary>How often the town draws (the "Motion" setting). Movement is timed by the clock, so
/// creatures walk at the same speed in every mode; only the smoothness changes.</summary>
public enum MotionMode { Smooth, Balanced, BatterySaver }

public static class Motion
{
    /// <summary>The choices as plugin.json lists them (Settings shows these words).</summary>
    public const string Smooth = "Smooth (60 fps)";
    public const string Balanced = "Balanced (30 fps)";
    public const string BatterySaver = "Battery saver (15 fps)";

    /// <summary>The setting's value; anything unknown is <see cref="MotionMode.Balanced"/>.</summary>
    public static MotionMode Parse(string? value)
    {
        var v = (value ?? "").Trim();
        if (v.StartsWith("Smooth", StringComparison.OrdinalIgnoreCase)) return MotionMode.Smooth;
        if (v.StartsWith("Battery", StringComparison.OrdinalIgnoreCase)) return MotionMode.BatterySaver;
        return MotionMode.Balanced;
    }

    public static int FramesPerSecond(MotionMode mode) => mode switch
    {
        MotionMode.Smooth => 60,
        MotionMode.BatterySaver => 15,
        _ => 30,
    };

    /// <summary>Time between drawn frames; a window that is not in front gets the battery-saver rate.</summary>
    public static TimeSpan Interval(MotionMode mode, bool focused)
        => TimeSpan.FromSeconds(1.0 / FramesPerSecond(focused ? mode : MotionMode.BatterySaver));
}

/// <summary>
/// Lets through one compositor frame per <see cref="Motion.Interval"/> and says how much time
/// passed since the last one let through. The screen offers frames at its own rate (60 Hz or
/// more); the canvases pass their own clock's time, which advances on every platform (the
/// frame-callback time of a headless window does not).
/// </summary>
public sealed class FrameGate
{
    private TimeSpan? _last;

    /// <summary>Forget the last frame (after a pause): the next one goes through with a nominal step.</summary>
    public void Reset() => _last = null;

    /// <summary>True when a frame at <paramref name="time"/> should step and draw; <paramref name="dt"/> is the time since the last one.</summary>
    public bool Pass(TimeSpan time, TimeSpan interval, out float dt)
    {
        if (_last is not { } last)
        {
            _last = time;
            dt = (float)interval.TotalSeconds;
            return true;
        }
        // A little slack: a 60 Hz screen's frames are 16.6 ms apart, so "every 33.3 ms" must
        // accept the second one even when it comes a hair early.
        if (time - last < interval - TimeSpan.FromMilliseconds(4))
        {
            dt = 0;
            return false;
        }
        dt = (float)(time - last).TotalSeconds;
        _last = time;
        return true;
    }
}
