using System;

namespace Noctis.Models;

/// <summary>
/// Which design the mini player wears. Classic is the size-morphing Liqoria card
/// (Icon / Bar / Card / Large icon / Lyrics); the other two are fixed designs from
/// community mockups (2026-09) that replace the size-driven forms while selected.
/// </summary>
public enum MiniPlayerStyle
{
    /// <summary>The resizable glass card with its five size-driven forms.</summary>
    Classic,
    /// <summary>Light pill: round cover overlapping a white card with title, artist and transport.</summary>
    Pill,
    /// <summary>Light sleeve: a spinning disc peeking out of the top of a rounded card, track text below.</summary>
    Sleeve,
}

public static class MiniPlayerStyles
{
    /// <summary>Setting value for the classic card — what every existing install shows.</summary>
    public const string DefaultSetting = nameof(MiniPlayerStyle.Classic);

    /// <summary>Parses the persisted setting string by NAME; anything unknown (including a bare
    /// number, which Enum.TryParse would otherwise accept) falls back to the classic card.</summary>
    public static MiniPlayerStyle Parse(string? setting)
    {
        var name = setting?.Trim();
        if (string.IsNullOrEmpty(name) || !char.IsLetter(name[0])) return MiniPlayerStyle.Classic;
        return Enum.TryParse<MiniPlayerStyle>(name, ignoreCase: true, out var style) && Enum.IsDefined(style)
            ? style
            : MiniPlayerStyle.Classic;
    }
}
