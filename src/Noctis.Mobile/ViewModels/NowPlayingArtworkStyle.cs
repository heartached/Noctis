using System;

namespace Noctis.Mobile.ViewModels;

/// <summary>How Now Playing shows the cover (Settings › Appearance › Now Playing Artwork).</summary>
public enum NowPlayingArtworkStyle
{
    /// <summary>The square cover, as it has always been.</summary>
    Cover,
    /// <summary>The cover as the centre label of a black record, spinning at 33⅓ while playing.</summary>
    Vinyl,
    /// <summary>The cover printed on a silver compact disc, turning slowly while playing.</summary>
    CD,
}

public static class NowPlayingArtworkStyles
{
    /// <summary>The choices in the order Settings lists them.</summary>
    public static readonly NowPlayingArtworkStyle[] All = { NowPlayingArtworkStyle.Cover, NowPlayingArtworkStyle.Vinyl, NowPlayingArtworkStyle.CD };

    /// <summary>The saved name back to a style; anything unknown (a hand-edited file, a newer
    /// version's value, a number) is the Cover.</summary>
    public static NowPlayingArtworkStyle Parse(string? saved)
    {
        var name = saved?.Trim();
        if (string.IsNullOrEmpty(name) || !char.IsLetter(name[0])) return NowPlayingArtworkStyle.Cover;
        return Enum.TryParse<NowPlayingArtworkStyle>(name, ignoreCase: true, out var style) && Enum.IsDefined(style)
            ? style
            : NowPlayingArtworkStyle.Cover;
    }
}
