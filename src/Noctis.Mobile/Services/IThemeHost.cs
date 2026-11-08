using Avalonia.Media;

namespace Noctis.Mobile.Services;

/// <summary>The app object that owns the resource dictionaries (AndroidApp); Settings asks it to re-theme.</summary>
public interface IThemeHost
{
    void ApplyTheme(string appearance, string darkTheme, string accentHex);

    /// <summary>The same, with Settings › Accent › Match Theme: while on, a colour theme's own
    /// accent is worn instead of <paramref name="accentHex"/> (<see cref="PhoneThemes.EffectiveAccent"/>;
    /// the host resolves System to Light or the dark theme first). By default the plain one.</summary>
    void ApplyTheme(string appearance, string darkTheme, string accentHex, bool accentFollowsTheme) =>
        ApplyTheme(appearance, darkTheme, accentHex);

    /// <summary>The same, with Settings › Accent › Match Artwork: while on, the playing cover's
    /// colour (<see cref="CoverColourChanged"/>) is worn instead of <paramref name="accentHex"/>,
    /// which stays the fallback (<see cref="PhoneThemes.EffectiveAccent(string, string, bool, bool, Color?)"/>).
    /// By default the one without it.</summary>
    void ApplyTheme(string appearance, string darkTheme, string accentHex, bool accentFollowsTheme, bool accentFollowsArtwork) =>
        ApplyTheme(appearance, darkTheme, accentHex, accentFollowsTheme);

    /// <summary>The playing song's cover colour changed: its vibrant colour, the desktop's "Accent
    /// follows album art" source (ShellViewModel, only while Match Artwork is on; null with nothing
    /// playing, no cover, or Match Artwork off). Under Match Artwork the host blends the accent into
    /// it (<see cref="MobileTheme.SetAccent"/>). No-op by default.</summary>
    void CoverColourChanged(Color? cover) { }

    /// <summary>
    /// Status bar icons for what a page draws under the bar: dark (true) on a light backdrop,
    /// light (false) on a dark one, null to follow the app theme again. The album page's cover
    /// runs under the bar, so the theme's choice can be unreadable there. No-op by default
    /// (tests, hosts without system bars).
    /// </summary>
    void SetStatusBarIcons(bool? dark) { }
}
