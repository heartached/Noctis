using System;

namespace Noctis.Mobile.ViewModels;

/// <summary>What Now Playing paints behind the cover (Settings › Appearance › Now Playing Background).</summary>
public enum NowPlayingBackgroundStyle
{
    /// <summary>The cover's colours as a soft wash, shaded toward the bottom, as it has always been.</summary>
    Wash,
    /// <summary>The cover itself, blurred over the whole screen, under a black tint (Discord #mobile-beta).</summary>
    BlurredCover,
}

public static class NowPlayingBackgroundStyles
{
    /// <summary>The choices in the order Settings lists them.</summary>
    public static readonly NowPlayingBackgroundStyle[] All = { NowPlayingBackgroundStyle.Wash, NowPlayingBackgroundStyle.BlurredCover };

    /// <summary>The saved name back to a style; anything unknown (a hand-edited file, a newer
    /// version's value, a number) is the Wash.</summary>
    public static NowPlayingBackgroundStyle Parse(string? saved)
    {
        var name = saved?.Trim();
        if (string.IsNullOrEmpty(name) || !char.IsLetter(name[0])) return NowPlayingBackgroundStyle.Wash;
        return Enum.TryParse<NowPlayingBackgroundStyle>(name, ignoreCase: true, out var style) && Enum.IsDefined(style)
            ? style
            : NowPlayingBackgroundStyle.Wash;
    }
}
