namespace Noctis.Mobile.Services;

/// <summary>
/// DIAGNOSTIC ONLY, all off by default. Switches that take one layer of the lyrics page away, so
/// the phone's allocation and render cost can be bisected (S23 11-23: 1-1.7 MB/s on the lyrics
/// page, where the headless tests saw a fraction). The Android head sets them once at start from
/// system properties (`adb shell setprop debug.noctis.lyr.noblur 1`, then restart the app):
/// debug.noctis.lyr.noblur, .nobands, .nobackdrop, .nodither. Read them with the stall monitor's
/// summary (debug.noctis.stalls). Never set in normal use; the page looks wrong with any of them.
/// </summary>
public static class LyricsDiagnostics
{
    /// <summary>No depth blur on any lyric line (the held-note glow keeps its own).</summary>
    public static bool NoLineBlur { get; set; }

    /// <summary>No top and bottom fade bands (WashFade) over the lines.</summary>
    public static bool NoBands { get; set; }

    /// <summary>No wash and no Blurred Cover behind the lyrics (the shade stays).</summary>
    public static bool NoBackdrop { get; set; }

    /// <summary>No dither layer over the lyrics' wash.</summary>
    public static bool NoDither { get; set; }

    /// <summary>Any switch is on (logged once at start).</summary>
    public static bool Any => NoLineBlur || NoBands || NoBackdrop || NoDither;
}
