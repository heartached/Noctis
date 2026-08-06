using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Runs a probe with the real accent overlay App.SetAccent builds merged onto the
/// headless application, so resources resolve exactly as they do in the running app.
/// </summary>
internal static class AccentTestHarness
{
    public static void WithAccent(string hex, Action probe) => WithAccent(hex, null, probe);

    /// <summary>
    /// <paramref name="variant"/> is applied before SetAccent runs, because SetAccent reads
    /// RequestedThemeVariant to decide theme-dependent entries — the now-playing row
    /// foreground among them. Pass null to leave the app on its default variant.
    /// </summary>
    public static void WithAccent(string hex, ThemeVariant? variant, Action probe)
    {
        var noctisApp = new Noctis.App();
        if (variant is not null)
            noctisApp.RequestedThemeVariant = variant;
        noctisApp.SetAccent(hex);
        var overlay = noctisApp.Resources.MergedDictionaries[^1];
        // A dictionary can only have one owner; hand it over to the headless app.
        noctisApp.Resources.MergedDictionaries.Remove(overlay);

        var app = Application.Current!;
        app.Resources.MergedDictionaries.Add(overlay);
        try { probe(); }
        finally { app.Resources.MergedDictionaries.Remove(overlay); }
    }

    public static Color ColorOf(IBrush? b) =>
        Assert.IsAssignableFrom<ISolidColorBrush>(b).Color;

    /// <summary>Resolves a brush resource the way a DynamicResource consumer would.</summary>
    public static Color ResourceColor(string key)
    {
        Assert.True(
            Application.Current!.Resources.TryGetResource(key, null, out var res),
            $"resource '{key}' not found");
        return ColorOf(res as IBrush);
    }
}
