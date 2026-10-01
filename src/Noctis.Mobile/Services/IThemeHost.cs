namespace Noctis.Mobile.Services;

/// <summary>The app object that owns the resource dictionaries (AndroidApp); Settings asks it to re-theme.</summary>
public interface IThemeHost
{
    void ApplyTheme(string appearance, string darkTheme, string accentHex);

    /// <summary>
    /// Status bar icons for what a page draws under the bar: dark (true) on a light backdrop,
    /// light (false) on a dark one, null to follow the app theme again. The album page's cover
    /// runs under the bar, so the theme's choice can be unreadable there. No-op by default
    /// (tests, hosts without system bars).
    /// </summary>
    void SetStatusBarIcons(bool? dark) { }
}
