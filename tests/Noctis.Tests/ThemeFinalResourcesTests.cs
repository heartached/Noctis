using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Plugins;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Whatever App.SetTheme / SetAccent do on the way (11-27: one coalesced resource update per
/// switch instead of the overlays being removed and re-added one by one), every theme x accent x
/// accent-window-tint combination must end on exactly the resources it ended on before. Each
/// combination is fingerprinted (every theme-, overlay- and accent-owned key resolved the way a
/// DynamicResource consumer resolves it, plus the theme variant) and compared with
/// ThemeFinalResources.golden.txt, written from the code before that change
/// (NOCTIS_WRITE_THEME_GOLDEN=1 rewrites it). The end state must also not depend on the theme
/// switched from, nor on whether the accent was set before or after the theme.
/// </summary>
public class ThemeFinalResourcesTests
{
    private static Noctis.App? s_app;

    private static readonly CustomThemeDefinition CustomDark = new()
    {
        Id = "golddark", Name = "Gold Dark", BaseMode = "Dark",
        MainBackgroundHex = "#1A1410", SidebarBackgroundHex = "#231B15", AccentHex = "#F4D24B",
    };

    private static readonly CustomThemeDefinition CustomLight = new()
    {
        Id = "paperlight", Name = "Paper Light", BaseMode = "Light",
        MainBackgroundHex = "#F4EFE6", SidebarBackgroundHex = "#EAE3D6", AccentHex = "#136F8C",
    };

    private static readonly PackTheme PackDusk = new("benchpack", "Bench Pack", "dusk", "Dusk", false, "#7C8CF8",
        new Dictionary<string, Color>
        {
            ["AppMainBackground"] = Color.Parse("#17131F"),
            ["AppSidebarBackground"] = Color.Parse("#1F1929"),
            ["PrimaryTextBrush"] = Color.Parse("#F1ECF7"),
        });

    private static readonly PackTheme PackPaper = new("benchpack", "Bench Pack", "paper", "Paper", true, null,
        new Dictionary<string, Color>
        {
            ["AppMainBackground"] = Color.Parse("#F7F3EA"),
            ["PrimaryTextBrush"] = Color.Parse("#2B2620"),
        });

    public static readonly string[] Themes =
    {
        Noctis.App.ThemeGray, Noctis.App.ThemeDark, Noctis.App.ThemeLight, Noctis.App.ThemeMidnight,
        Noctis.App.ThemeInk, Noctis.App.ThemeSmoke,
        "Custom:" + CustomDark.Id, "Custom:" + CustomLight.Id,
        Noctis.App.PackThemePrefix + PackDusk.Key, Noctis.App.PackThemePrefix + PackPaper.Key,
    };

    private static readonly string[] Accents = { "#E74856", "#2A7FCF" };

    private static Noctis.App RealApp()
    {
        if (s_app == null)
        {
            var app = new Noctis.App();
            app.Initialize();
            app.CustomThemeResolver = id => id == CustomDark.Id ? CustomDark : id == CustomLight.Id ? CustomLight : null;
            app.PackThemeResolver = key => key == PackDusk.Key ? PackDusk : key == PackPaper.Key ? PackPaper : null;
            s_app = app;
        }
        return s_app;
    }

    /// <summary>Every key a theme, an overlay, a custom / pack theme or the accent overlay owns.</summary>
    private static IReadOnlyList<string> ThemeKeys(Noctis.App app)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        // Fluent's and Assets/Styles.axaml's resources, every variant (as App.Initialize loaded them).
        foreach (var style in app.Styles)
            CollectKeys(style, keys);
        foreach (var overlay in new[] { "Dark", "Midnight", "Ink", "Smoke" })
            keys.UnionWith(ThemeOverlayParityTests.LoadOverlay(overlay).Keys.OfType<string>());
        foreach (var light in new[] { false, true })
            keys.UnionWith(Noctis.Helpers.AccentPalette.Build(Color.Parse("#E74856"), light).Keys.OfType<string>());
        keys.UnionWith(AccentWindowTint.Keys);
        keys.UnionWith(ThemeDerivation.Derive(CustomDark).Keys);
        keys.UnionWith(ThemeDerivation.Derive(CustomLight).Keys);
        keys.UnionWith(PackThemeBuilder.Build(PackDusk).Keys);
        keys.UnionWith(PackThemeBuilder.Build(PackPaper).Keys);
        keys.RemoveWhere(k => k.StartsWith("__", StringComparison.Ordinal));
        return keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    private static void CollectKeys(object? node, HashSet<string> keys)
    {
        switch (node)
        {
            case StyleInclude include:
                CollectKeys(include.Loaded, keys);
                break;
            case ResourceInclude include:
                CollectKeys(include.Loaded, keys);
                break;
            case ResourceDictionary dict:
                keys.UnionWith(dict.Keys.OfType<string>());
                foreach (var merged in dict.MergedDictionaries) CollectKeys(merged, keys);
                foreach (var themed in dict.ThemeDictionaries.Values) CollectKeys(themed, keys);
                break;
            case Styles styles:
                CollectKeys(styles.Resources, keys);
                foreach (var child in styles) CollectKeys(child, keys);
                break;
            case StyleBase style:
                CollectKeys(style.Resources, keys);
                foreach (var child in style.Children) CollectKeys(child, keys);
                break;
        }
    }

    private static string Describe(object? v) => v switch
    {
        null => "null",
        ISolidColorBrush b => $"SCB {b.Color} @{F(b.Opacity)}",
        ILinearGradientBrush g => $"LGB {g.StartPoint} {g.EndPoint} @{F(g.Opacity)} [{Stops(g)}]",
        IRadialGradientBrush g => $"RGB {g.Center} {g.RadiusX} {g.RadiusY} @{F(g.Opacity)} [{Stops(g)}]",
        IGradientBrush g => $"{g.GetType().Name} @{F(g.Opacity)} [{Stops(g)}]",
        Color c => $"C {c}",
        double d => $"D {F(d)}",
        IFormattable f => $"{v.GetType().Name} {f.ToString(null, CultureInfo.InvariantCulture)}",
        _ => $"{v.GetType().Name} {v}",
    };

    private static string F(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    private static string Stops(IGradientBrush g) =>
        string.Join(",", g.GradientStops.Select(s => $"{s.Color}:{F(s.Offset)}"));

    /// <summary>The resources as the app's controls see them, as one hash (plus the dump).</summary>
    private static (string Hash, string Dump) Fingerprint(Noctis.App app, IReadOnlyList<string> keys)
    {
        var sb = new StringBuilder();
        sb.Append("variant=").Append(app.RequestedThemeVariant).Append('\n');
        foreach (var key in keys)
        {
            sb.Append(key).Append('=');
            sb.Append(app.TryGetResource(key, app.ActualThemeVariant, out var v) ? Describe(v) : "<missing>");
            sb.Append('\n');
        }
        var dump = sb.ToString();
        return (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)))[..16], dump);
    }

    private static string GoldenPath([CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "ThemeFinalResources.golden.txt");

    private static void Reset(Noctis.App app)
    {
        app.SetTintWindowWithAccent(false);
        app.SetAccent(Accents[0]);
        app.SetTheme(Noctis.App.ThemeGray);
    }

    [AvaloniaFact]
    public void EveryThemeAccentAndTint_EndsOnTheSameResourcesAsBefore()
    {
        var app = RealApp();
        var keys = ThemeKeys(app);
        var lines = new List<string>();
        try
        {
            foreach (var tint in new[] { false, true })
            foreach (var accent in Accents)
            foreach (var to in Themes)
            {
                // Reference path: from Gray, accent set first (a custom theme then applies its own).
                Reset(app);
                app.SetTintWindowWithAccent(tint);
                app.SetAccent(accent);
                app.SetTheme(to);
                var (hash, dump) = Fingerprint(app, keys);
                lines.Add($"{(tint ? "tint" : "plain")}\t{accent}\t{to}\t{hash}");

                // The same end state from every other theme, and with the accent set afterwards.
                foreach (var from in Themes)
                {
                    Reset(app);
                    app.SetTintWindowWithAccent(tint);
                    app.SetAccent(Accents[1 - Array.IndexOf(Accents, accent)]);
                    app.SetTheme(from);
                    app.SetAccent(accent);
                    app.SetTheme(to);
                    var (other, otherDump) = Fingerprint(app, keys);
                    if (other != hash)
                    {
                        var diff = dump.Split('\n').Zip(otherDump.Split('\n')).Where(p => p.First != p.Second).Take(8)
                            .Select(p => $"  {p.First}  vs  {p.Second}");
                        Assert.Fail($"{(tint ? "tint" : "plain")} {accent} {to} from {from} differs:\n{string.Join("\n", diff)}");
                    }
                }
            }
        }
        finally
        {
            Reset(app);
        }

        var path = GoldenPath();
        if (Environment.GetEnvironmentVariable("NOCTIS_WRITE_THEME_GOLDEN") == "1")
        {
            File.WriteAllText(path, string.Join("\n", lines) + "\n");
            return;
        }
        var golden = File.ReadAllLines(path).Where(l => l.Length > 0).ToList();
        Assert.Equal(golden.Count, lines.Count);
        var mismatches = golden.Zip(lines).Where(p => p.First != p.Second).Select(p => $"golden {p.First} | now {p.Second}").ToList();
        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
    }
}
