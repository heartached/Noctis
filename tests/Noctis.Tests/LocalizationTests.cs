using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Noctis.Localization;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The localization scaffold: English fallback, live culture switch, key-on-miss, and two
/// source-level guards — every {loc:T} key in XAML exists in Strings.resx, and every
/// translation file only contains keys that exist in English.
/// </summary>
[Collection("Localization")] // Loc.Instance is process-wide: these tests must not interleave
// No constructor/Dispose reset: those run off the headless UI thread, and a culture switch from
// there reaches leftover {loc:T} bindings and Relabel handlers of other tests' views ("a different
// thread owns it"). Each test that switches the culture puts English back on the UI thread.
public class LocalizationTests
{
    [AvaloniaFact]
    public void English_IsTheDefault_AndMissingKeysShowTheKey()
    {
        Loc.Instance.SetCulture("en");
        Assert.Equal("Home", Loc.T("Nav.Home"));
        Assert.Equal("Nope.Missing", Loc.T("Nope.Missing"));
        Assert.Equal("Home", Loc.Instance["Nav.Home"]);
    }

    // On the headless UI thread: leftover {loc:T} bindings from other UI tests are
    // weak-subscribed to Loc.Instance, and Avalonia 12 rejects a property set from
    // any other thread. The app only switches culture from the Settings picker.
    [AvaloniaFact]
    public void SwitchingCulture_RaisesIndexerChange_AndFallsBackPerKey()
    {
        // Start from English BEFORE counting: the process starts on the OS UI culture
        // ("en-US" on a typical machine), so this first switch is itself a real change and
        // counting it made the result depend on the machine locale and test order.
        Loc.Instance.SetCulture("en");

        var raised = new List<string?>();
        Loc.Instance.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        var cultureEvents = 0;
        Loc.Instance.CultureChanged += (_, _) => cultureEvents++;

        try
        {
            Loc.Instance.SetCulture("es");
            Assert.Equal("Inicio", Loc.T("Nav.Home"));
            Assert.Contains("Item", raised); // the CLR indexer name: what Avalonia's indexer binding listens for
            Assert.Equal(1, cultureEvents);

            // Same culture again is a no-op.
            Loc.Instance.SetCulture("es");
            Assert.Equal(1, cultureEvents);

            // Unknown culture name → OS language, never a crash.
            Loc.Instance.SetCulture("zz-NOPE");
            Assert.NotNull(Loc.Instance.Culture);
        }
        finally { Loc.Instance.SetCulture("en"); }
    }

    [Fact]
    public void Supported_IsDiscoveredFromSatelliteAssemblies()
    {
        // The test output carries es/Noctis.UI.resources.dll from Strings.es.resx.
        Assert.Equal("en", Loc.Supported[0]);
        Assert.Contains("es", Loc.Supported);
    }

    [Theory]
    [InlineData("es", "es")]
    [InlineData("es-MX", "es")]
    [InlineData("en-GB", "en")]
    [InlineData("fr", "fr")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("ru-RU", "ru")]
    [InlineData("de-AT", "de")]
    [InlineData("pt-BR", "en")] // no Portuguese → English
    public void Resolve_MapsASettingToAShippedTranslation(string setting, string expected)
        => Assert.Equal(expected, Loc.Resolve(setting));

    [Fact]
    public void EveryXamlLocKey_ExistsInEnglishStrings()
    {
        var root = FindRepoRoot();
        var english = ReadKeys(Path.Combine(root, "src", "Noctis.UI", "Localization", "Strings.resx"));
        var used = new[] { "Noctis", "Noctis.UI", "Noctis.Mobile" }
            .Select(proj => Path.Combine(root, "src", proj))
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.axaml", SearchOption.AllDirectories))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(p => Regex.Matches(File.ReadAllText(p), @"\{loc:T\s+([A-Za-z0-9_.]+)").Select(m => m.Groups[1].Value))
            .Distinct()
            .ToList();

        Assert.NotEmpty(used);
        var missing = used.Where(k => !english.Contains(k)).ToList();
        Assert.True(missing.Count == 0, "Keys used in XAML but missing from Strings.resx: " + string.Join(", ", missing));
    }

    /// <summary>
    /// GenerateResource dedupes names case-insensitively ("Duplicate resource name … ignored"),
    /// so a key that differs from another only by case is silently dropped from the compiled
    /// resources and renders as its raw key (Settings.Username next to Settings.USERNAME did).
    /// </summary>
    [Fact]
    public void EnglishKeys_AreUniqueIgnoringCase()
    {
        var path = Path.Combine(FindRepoRoot(), "src", "Noctis.UI", "Localization", "Strings.resx");
        var clashes = ReadKeys(path)
            .GroupBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => string.Join(" / ", g))
            .ToList();
        Assert.True(clashes.Count == 0, "Keys that differ only by case (the later one is dropped by resgen): " + string.Join(", ", clashes));
    }

    [Fact]
    public void TranslationFiles_OnlyContainEnglishKeys()
    {
        var dir = Path.Combine(FindRepoRoot(), "src", "Noctis.UI", "Localization");
        var english = ReadKeys(Path.Combine(dir, "Strings.resx"));
        foreach (var file in Directory.GetFiles(dir, "Strings.*.resx"))
        {
            var stray = ReadKeys(file).Where(k => !english.Contains(k)).ToList();
            Assert.True(stray.Count == 0, $"{Path.GetFileName(file)} has keys not in Strings.resx: {string.Join(", ", stray)}");
            var culture = Path.GetFileNameWithoutExtension(file).Split('.')[1];
            Assert.NotNull(System.Globalization.CultureInfo.GetCultureInfo(culture)); // a real culture name, so the satellite loads
        }
    }

    [Fact]
    public void KnownCultures_ListsEveryShippedTranslation()
    {
        // Android has no satellite directory to scan (assemblies are embedded in the APK),
        // so Loc.Supported falls back to this compiled-in list. It must name every
        // Strings.<culture>.resx in the tree or that language is missing from the phone's picker.
        var dir = Path.Combine(FindRepoRoot(), "src", "Noctis.UI", "Localization");
        var shipped = Directory.GetFiles(dir, "Strings.*.resx")
            .Select(f => Path.GetFileNameWithoutExtension(f)["Strings.".Length..])
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(shipped, Loc.KnownCultures.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList());
    }

    [AvaloniaFact]
    public void Russian_LoadsFromItsSatelliteAssembly()
    {
        try
        {
            Loc.Instance.SetCulture("ru");
            Assert.Equal("Главная", Loc.T("Nav.Home"));
            Assert.Equal("Песен: 12", Loc.T("QueueToast.SongCount", 12));
        }
        finally { Loc.Instance.SetCulture("en"); }
    }

    /// <summary>
    /// Translations that ship complete (Russian, GitHub #128) must carry every English key and
    /// nothing else, with the same {0} / {1:N0} placeholders per key: a dropped placeholder loses
    /// a value on screen and an extra one makes string.Format throw.
    /// </summary>
    [Theory]
    [InlineData("ru")]
    [InlineData("de")]
    public void CompleteTranslation_MatchesEnglishKeysAndPlaceholders(string culture)
    {
        var dir = Path.Combine(FindRepoRoot(), "src", "Noctis.UI", "Localization");
        var english = ReadValues(Path.Combine(dir, "Strings.resx"));
        var translated = ReadValues(Path.Combine(dir, $"Strings.{culture}.resx"));

        var missing = english.Keys.Where(k => !translated.ContainsKey(k)).ToList();
        var extra = translated.Keys.Where(k => !english.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, $"Strings.{culture}.resx is missing: {string.Join(", ", missing)}");
        Assert.True(extra.Count == 0, $"Strings.{culture}.resx has keys not in Strings.resx: {string.Join(", ", extra)}");

        static string Placeholders(string value) => string.Join(" ",
            Regex.Matches(value, @"\{[^{}]*\}").Select(m => m.Value).Distinct().OrderBy(p => p, StringComparer.Ordinal));
        var mismatched = english
            .Where(e => Placeholders(e.Value) != Placeholders(translated[e.Key]))
            .Select(e => $"{e.Key} [{Placeholders(e.Value)}] vs [{Placeholders(translated[e.Key])}]")
            .ToList();
        Assert.True(mismatched.Count == 0, "Placeholder mismatch: " + string.Join("; ", mismatched));
    }

    /// <summary>
    /// The song menu's Lyrics ▸ Open in Lyrics Studio and Lyrics Background Video ▸ Choose video
    /// rows read without a trailing ellipsis (owner 10-27), in English and every translation.
    /// </summary>
    [Theory]
    [InlineData("Menu.LyricsStudio")]
    [InlineData("Menu.ChooseSongVideo")]
    [InlineData("AlbumDetail.ChooseAlbumVideo")]
    public void SongMenuLyricsRows_HaveNoTrailingEllipsis(string key)
    {
        var dir = Path.Combine(FindRepoRoot(), "src", "Noctis.UI", "Localization");
        Assert.True(ReadValues(Path.Combine(dir, "Strings.resx")).ContainsKey(key), $"{key} missing from Strings.resx");
        foreach (var file in Directory.GetFiles(dir, "Strings*.resx"))
            if (ReadValues(file).TryGetValue(key, out var value))
                Assert.False(value.TrimEnd().EndsWith('…') || value.TrimEnd().EndsWith("..."),
                    $"{Path.GetFileName(file)} {key} = \"{value}\" ends in an ellipsis");
    }

    /// <summary>
    /// The phone's Hidden row sits in Settings' Library group (SettingsPage.axaml, caption
    /// Tab.Library), so the banner after Hide Song names Settings › Library › Hidden in every
    /// language, with the same words as the group caption and the row.
    /// </summary>
    [Fact]
    public void MobileHiddenNotice_NamesTheLibraryGroupAndTheHiddenRow()
    {
        var dir = Path.Combine(FindRepoRoot(), "src", "Noctis.UI", "Localization");
        foreach (var file in Directory.GetFiles(dir, "Strings*.resx"))
        {
            var values = ReadValues(file);
            var notice = values["Mobile.HiddenNotice"];
            var library = values.TryGetValue("Tab.Library", out var l) ? l : ReadValues(Path.Combine(dir, "Strings.resx"))["Tab.Library"];
            var hidden = values["Mobile.Settings.Hidden"];
            var path = library.Trim() + " › " + hidden.Trim();
            Assert.True(notice.Contains(path, StringComparison.Ordinal),
                $"{Path.GetFileName(file)} Mobile.HiddenNotice = \"{notice}\" doesn't name \"{path}\"");
        }
    }

    [Theory]
    [InlineData("LyricShare.LyricsPage")]
    [InlineData("ShareLyrics.PageRangeSummary")]
    [InlineData("LyricShare.SafeZones")]
    public void LyricsPageKeys_ExistInEnglish(string key)
        => Assert.True(ReadValues(Path.Combine(FindRepoRoot(), "src", "Noctis.UI", "Localization", "Strings.resx")).ContainsKey(key));

    private static Dictionary<string, string> ReadValues(string resxPath)
        => XDocument.Load(resxPath).Root!
            .Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "", StringComparer.Ordinal);

    private static HashSet<string> ReadKeys(string resxPath)
        => XDocument.Load(resxPath).Root!
            .Elements("data")
            .Select(d => (string)d.Attribute("name")!)
            .ToHashSet(StringComparer.Ordinal);

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Noctis.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }
}
