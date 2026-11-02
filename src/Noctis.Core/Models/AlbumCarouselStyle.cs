using System;

namespace Noctis.Models;

/// <summary>
/// What each album looks like in the album carousel (Albums page / artist page, GitHub #149):
/// the bare cover, or the cover dressed as a physical release. All drawn with vector shapes.
/// </summary>
public enum AlbumCarouselStyle
{
    /// <summary>The cover alone, with a floor reflection (the default).</summary>
    Cover,
    /// <summary>The cover behind the clear lid of a CD jewel case, hinge spine on the left.</summary>
    JewelCase,
    /// <summary>The cover as a record sleeve, the disc peeking out on the right.</summary>
    Vinyl,
    /// <summary>A cassette shell: the cover on the label, reels in the window below.</summary>
    Cassette,
}

public static class AlbumCarouselStyles
{
    /// <summary>Setting value for the plain cover — what a fresh install shows.</summary>
    public const string DefaultSetting = nameof(AlbumCarouselStyle.Cover);

    /// <summary>Parses the persisted setting by NAME; anything unknown (including a bare
    /// number, which Enum.TryParse would accept) is the plain cover.</summary>
    public static AlbumCarouselStyle Parse(string? setting)
    {
        var name = setting?.Trim();
        if (string.IsNullOrEmpty(name) || !char.IsLetter(name[0])) return AlbumCarouselStyle.Cover;
        return Enum.TryParse<AlbumCarouselStyle>(name, ignoreCase: true, out var style) && Enum.IsDefined(style)
            ? style
            : AlbumCarouselStyle.Cover;
    }
}
