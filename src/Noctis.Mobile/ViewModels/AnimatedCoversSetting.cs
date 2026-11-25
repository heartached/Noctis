using System;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// Settings › Appearance › Animated Covers (Discord beta 11-23, "no animated cover support"):
/// when Now Playing's cover moves. A loop beside the phone's own music, or one already saved from
/// the computer, plays under Wi-Fi Only and Always alike (it costs no data); the setting decides
/// when a computer's loop (tens of MB) may be downloaded.
/// </summary>
public enum AnimatedCoversSetting
{
    /// <summary>The still cover, always; nothing downloaded.</summary>
    Off,
    /// <summary>The default: the computer's loops download on Wi-Fi (an unmetered network) only.</summary>
    WifiOnly,
    /// <summary>The computer's loops download on mobile data too.</summary>
    Always,
}

public static class AnimatedCoversSettings
{
    /// <summary>The choices in the order Settings lists them.</summary>
    public static readonly AnimatedCoversSetting[] All = { AnimatedCoversSetting.Off, AnimatedCoversSetting.WifiOnly, AnimatedCoversSetting.Always };

    /// <summary>The saved name back to a choice; anything unknown (a hand-edited file, a newer
    /// version's value, a number) is the default, Wi-Fi Only.</summary>
    public static AnimatedCoversSetting Parse(string? saved)
    {
        var name = saved?.Trim();
        if (string.IsNullOrEmpty(name) || !char.IsLetter(name[0])) return AnimatedCoversSetting.WifiOnly;
        return Enum.TryParse<AnimatedCoversSetting>(name, ignoreCase: true, out var setting) && Enum.IsDefined(setting)
            ? setting
            : AnimatedCoversSetting.WifiOnly;
    }
}
