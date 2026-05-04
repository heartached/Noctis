using Noctis.Models;

namespace Noctis.Services;

public static class AutoMixFadeMath
{
    public static double SmoothFadeProgress(double progress)
    {
        progress = Math.Clamp(progress, 0.0, 1.0);
        return progress * progress * (3 - (2 * progress));
    }

    public static (double Out, double In) GetFadeFactors(double progress, AutoMixFadeCurve fadeCurve)
    {
        progress = Math.Clamp(progress, 0.0, 1.0);
        if (fadeCurve == AutoMixFadeCurve.EqualPower)
        {
            var angle = progress * Math.PI / 2.0;
            return (Math.Cos(angle), Math.Sin(angle));
        }

        var eased = SmoothFadeProgress(progress);
        return (1.0 - eased, eased);
    }
}
