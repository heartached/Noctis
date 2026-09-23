namespace Noctis.Mobile.Services;

/// <summary>The app object that owns the resource dictionaries (AndroidApp); Settings asks it to re-theme.</summary>
public interface IThemeHost
{
    void ApplyTheme(string appearance, string darkTheme, string accentHex);
}
