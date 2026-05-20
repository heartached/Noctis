using System;

namespace Noctis.Helpers;

/// <summary>Shared easing functions for lyric scroll animations.</summary>
public static class Easing
{
    /// <summary>
    /// Cubic ease-in-out (Ken Perlin's smootherstep). Glides in and out instead
    /// of starting or stopping abruptly. Input is clamped to [0, 1].
    /// </summary>
    public static double SmootherStep(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return t * t * t * (t * (t * 6 - 15) + 10);
    }
}
