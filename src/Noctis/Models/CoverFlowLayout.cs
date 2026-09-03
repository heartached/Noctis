using System;

namespace Noctis.Models;

/// <summary>
/// How the Albums → Cover Flow page arranges the queue's covers.
/// </summary>
public enum CoverFlowLayout
{
    /// <summary>The classic row: playing cover centred, history and up-next receding to each side.</summary>
    Carousel,
    /// <summary>Tilted cards on a diagonal path — history above, up-next below the playing cover,
    /// track text beside the pile (community mockup, 2026-08).</summary>
    Cascade,
    /// <summary>Static mosaic of the queue's covers, no text.</summary>
    Collage,
}

public static class CoverFlowLayouts
{
    /// <summary>Setting value for the classic row — what every existing install shows.</summary>
    public const string DefaultSetting = nameof(CoverFlowLayout.Carousel);

    /// <summary>Parses the persisted setting string by NAME; anything unknown (including a bare
    /// number, which Enum.TryParse would otherwise accept) falls back to the carousel.</summary>
    public static CoverFlowLayout Parse(string? setting)
    {
        var name = setting?.Trim();
        if (string.IsNullOrEmpty(name) || !char.IsLetter(name[0])) return CoverFlowLayout.Carousel;
        return Enum.TryParse<CoverFlowLayout>(name, ignoreCase: true, out var layout) && Enum.IsDefined(layout)
            ? layout
            : CoverFlowLayout.Carousel;
    }

    /// <summary>The layout the top-bar pill segment steps to: Carousel → Cascade → Collage → Carousel.</summary>
    public static CoverFlowLayout Next(CoverFlowLayout layout) => layout switch
    {
        CoverFlowLayout.Carousel => CoverFlowLayout.Cascade,
        CoverFlowLayout.Cascade => CoverFlowLayout.Collage,
        _ => CoverFlowLayout.Carousel,
    };
}
