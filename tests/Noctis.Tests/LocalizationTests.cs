using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Noctis.Localization;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The localization scaffold: English fallback, live culture switch, key-on-miss, and two
/// source-level guards — every {loc:T} key in XAML exists in Strings.resx, and every
/// translation file only contains keys that exist in English.
/// </summary>
[Collection("Localization")] // Loc.Instance is process-wide: these tests must not interleave
public class LocalizationTests : IDisposable
{
    public LocalizationTests() => Loc.Instance.SetCulture("en");
    public void Dispose() => Loc.Instance.SetCulture("en");

    [Fact]
    public void English_IsTheDefault_AndMissingKeysShowTheKey()
    {
        Assert.Equal("Home", Loc.T("Nav.Home"));
        Assert.Equal("Nope.Missing", Loc.T("Nope.Missing"));
        Assert.Equal("Home", Loc.Instance["Nav.Home"]);
    }

    [Fact]
    public void SwitchingCulture_RaisesIndexerChange_AndFallsBackPerKey()
    {
        var raised = new List<string?>();
        Loc.Instance.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        var cultureEvents = 0;
        Loc.Instance.CultureChanged += (_, _) => cultureEvents++;

        Loc.Instance.SetCulture("es");
        Assert.Equal("Inicio", Loc.T("Nav.Home"));
        Assert.Contains("Item[]", raised);
        Assert.Equal(1, cultureEvents);

        // Same culture again is a no-op.
        Loc.Instance.SetCulture("es");
        Assert.Equal(1, cultureEvents);

        // Unknown culture name → OS language, never a crash.
        Loc.Instance.SetCulture("zz-NOPE");
        Assert.NotNull(Loc.Instance.Culture);
    }

    [Theory]
    [InlineData("es", "es")]
    [InlineData("es-MX", "es")]
    [InlineData("en-GB", "en")]
    [InlineData("fr", "en")] // no French yet → English
    public void Resolve_MapsASettingToAShippedTranslation(string setting, string expected)
        => Assert.Equal(expected, Loc.Resolve(setting));

    [Fact]
    public void EveryXamlLocKey_ExistsInEnglishStrings()
    {
        var root = FindRepoRoot();
        var english = ReadKeys(Path.Combine(root, "src", "Noctis", "Localization", "Strings.resx"));
        var used = Directory.EnumerateFiles(Path.Combine(root, "src", "Noctis"), "*.axaml", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(p => Regex.Matches(File.ReadAllText(p), @"\{loc:T\s+([A-Za-z0-9_.]+)").Select(m => m.Groups[1].Value))
            .Distinct()
            .ToList();

        Assert.NotEmpty(used);
        var missing = used.Where(k => !english.Contains(k)).ToList();
        Assert.True(missing.Count == 0, "Keys used in XAML but missing from Strings.resx: " + string.Join(", ", missing));
    }

    [Fact]
    public void TranslationFiles_OnlyContainEnglishKeys()
    {
        var dir = Path.Combine(FindRepoRoot(), "src", "Noctis", "Localization");
        var english = ReadKeys(Path.Combine(dir, "Strings.resx"));
        foreach (var file in Directory.GetFiles(dir, "Strings.*.resx"))
        {
            var stray = ReadKeys(file).Where(k => !english.Contains(k)).ToList();
            Assert.True(stray.Count == 0, $"{Path.GetFileName(file)} has keys not in Strings.resx: {string.Join(", ", stray)}");
            var culture = Path.GetFileNameWithoutExtension(file).Split('.')[1];
            Assert.Contains(culture, Loc.Supported);
        }
    }

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
