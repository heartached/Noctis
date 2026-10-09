using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Every screen, for every theme: each {DynamicResource Key} any view, control or shared
/// style consumes must resolve under the theme's variant with the theme overlay merged —
/// through Fluent, Assets/Styles.axaml, the overlay and the accent overlay — exactly the
/// chain the running app uses. A key that fails here is a DynamicResource consumer that
/// would silently render with no brush on that theme.
/// </summary>
public class ThemeKeyCoverageTests
{
    private static readonly Regex DynamicRef = new(@"\{DynamicResource\s+([A-Za-z0-9_.]+)\}", RegexOptions.Compiled);
    private static readonly Regex LocalKey = new(@"x:Key=""([A-Za-z0-9_.]+)""", RegexOptions.Compiled);

    public static IEnumerable<object[]> Themes() => new[]
    {
        new object[] { "Ink", false }, new object[] { "Smoke", false },
    };

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "Noctis", "Views")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repo root from " + AppContext.BaseDirectory);
    }

    /// <summary>Per source file: the dynamic keys it consumes that it does not define itself.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ConsumedKeysByFile()
    {
        var root = FindRepoRoot();
        var src = Path.Combine(root, "src", "Noctis");
        var ui = Path.Combine(root, "src", "Noctis.UI"); // shared styles and controls
        var files = new[] { Path.Combine(src, "Views"), Path.Combine(src, "Controls"), Path.Combine(ui, "Controls") }
            .SelectMany(d => Directory.EnumerateFiles(d, "*.axaml", SearchOption.AllDirectories))
            .Append(Path.Combine(ui, "Assets", "Styles.axaml"))
            .Append(Path.Combine(ui, "Assets", "PillDialog.axaml")) // pill pop-up styles (PillDialogHost)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

        var result = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var local = LocalKey.Matches(text).Select(m => m.Groups[1].Value).ToHashSet();
            var consumed = DynamicRef.Matches(text).Select(m => m.Groups[1].Value)
                .Where(k => !local.Contains(k)).Distinct().OrderBy(k => k).ToList();
            result[Path.GetRelativePath(src, file)] = consumed;
        }
        return result;
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public void EveryDynamicKeyOnEveryScreen_Resolves(string theme, bool light)
    {
        var app = Application.Current!;
        // Styles.axaml (+ font/icon prerequisites) exactly as the view-mount tests load it.
        _ = ThemeOverlayParityTests.BaseDarkKeys();
        if (!app.Styles.OfType<StyleInclude>().Any(s => s.Source?.ToString().EndsWith("Styles.axaml") == true))
            app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });

        // The icon dictionaries App.axaml merges next to Icons.axaml (pages, pop-ups, menus).
        foreach (var name in new[] { "IconsPages", "IconsLine", "IconsMenuExtra" })
            if (!app.Resources.MergedDictionaries.OfType<ResourceInclude>().Any(r => r.Source?.ToString().EndsWith(name + ".axaml") == true))
                app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri($"avares://Noctis.UI/Assets/{name}.axaml") });

        var variant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        var overlay = new ResourceInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri($"avares://Noctis.UI/Assets/Themes/{theme}.axaml"),
        };
        var previousVariant = app.RequestedThemeVariant;
        app.RequestedThemeVariant = variant;
        app.Resources.MergedDictionaries.Add(overlay);
        try
        {
            var unresolved = new List<string>();
            AccentTestHarness.WithAccent("#E74856", variant, () =>
            {
                foreach (var (file, keys) in ConsumedKeysByFile())
                    foreach (var key in keys)
                        if (!app.TryGetResource(key, variant, out _))
                            unresolved.Add($"{file}: {key}");
            });
            Assert.True(unresolved.Count == 0,
                $"{theme}: {unresolved.Count} unresolved DynamicResource keys\n  " + string.Join("\n  ", unresolved));
        }
        finally
        {
            app.Resources.MergedDictionaries.Remove(overlay);
            app.RequestedThemeVariant = previousVariant;
        }
    }
}
