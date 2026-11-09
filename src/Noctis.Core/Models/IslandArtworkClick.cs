using System;

namespace Noctis.Models;

/// <summary>
/// What a click on the player island's artwork opens (GitHub #183, Settings → Player →
/// Clicking the artwork). The mini player is what the artwork always opened, so it stays
/// the default; the other two follow the Apple Music web player, where the artwork opens
/// the lyrics / full-screen player.
/// </summary>
public enum IslandArtworkClick
{
    /// <summary>Toggles the mini player (the behaviour before #183).</summary>
    MiniPlayer,
    /// <summary>Opens the lyrics page (the island's lyrics button does the same).</summary>
    LyricsPage,
    /// <summary>Opens the lyrics page with the window in full screen (sidebar hidden).</summary>
    FullScreenLyrics,
}

public static class IslandArtworkClicks
{
    /// <summary>Setting value every existing install keeps: the mini player.</summary>
    public const string DefaultSetting = nameof(IslandArtworkClick.MiniPlayer);

    /// <summary>Parses the persisted setting by NAME; anything unknown (including a bare
    /// number, which Enum.TryParse would otherwise accept) is the mini player.</summary>
    public static IslandArtworkClick Parse(string? setting)
    {
        var name = setting?.Trim();
        if (string.IsNullOrEmpty(name) || !char.IsLetter(name[0])) return IslandArtworkClick.MiniPlayer;
        return Enum.TryParse<IslandArtworkClick>(name, ignoreCase: true, out var action) && Enum.IsDefined(action)
            ? action
            : IslandArtworkClick.MiniPlayer;
    }

    /// <summary>The click lands on a lyrics page (windowed or full screen).</summary>
    public static bool OpensLyrics(this IslandArtworkClick action) => action != IslandArtworkClick.MiniPlayer;
}
