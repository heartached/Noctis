using System;
using System.Linq;
using Avalonia;
using Avalonia.Markup.Xaml.Styling;

namespace Noctis.Helpers;

/// <summary>
/// Noctis's look for a plugin's own window (Assets/PluginView.axaml): the Settings switch for
/// every CheckBox, pill buttons, fields and lists, and frosted-glass menus and drop-downs.
///
/// Why the host applies it to the window instead of each plugin including it: every plugin view,
/// official or third-party, old or new, then matches the app with no code of its own, and plugins
/// keep depending on the SDK only (they never reference Noctis.UI). A plugin's own styles still
/// win, so one that draws its own controls is left alone.
/// </summary>
public static class PluginViewTheme
{
    /// <summary>The class the themed window carries.</summary>
    public const string WindowClass = "plugin-view";

    public static readonly Uri Source = new("avares://Noctis.UI/Assets/PluginView.axaml");

    /// <summary>Adds the theme to <paramref name="target"/> (a plugin window) once.</summary>
    public static void Apply(StyledElement target)
    {
        if (IsApplied(target)) return;
        target.Styles.Add(new StyleInclude(new Uri("avares://Noctis.UI/")) { Source = Source });
        target.Classes.Add(WindowClass);
    }

    public static bool IsApplied(StyledElement target)
        => target.Styles.OfType<StyleInclude>().Any(s => s.Source == Source);
}
