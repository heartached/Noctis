using System.Runtime.Loader;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Noctis.Localization;
using Noctis.Services;
using Noctis.Services.Plugins;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>Shared helpers: the repository's sample content pack and small hand-made packs.</summary>
internal static class ContentPackKit
{
    public const string SampleId = "dev.noctis.samples.contentpack";
    public static string SampleDir => PluginSandbox.RepoFile("samples/Noctis.ContentPack.Sample/pack");

    /// <summary>Copies the sample pack into plugins/&lt;folder&gt;.</summary>
    public static string CopySample(PluginSandbox box, string folder = SampleId)
    {
        var target = Path.Combine(box.PluginsDir, folder);
        foreach (var file in Directory.GetFiles(SampleDir, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(SampleDir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
        return target;
    }

    /// <summary>The sample pack's files as zip entries (forward slashes), plus extras.</summary>
    public static (string, byte[])[] SampleEntries(string prefix = "", params (string, byte[])[] extra)
        => Directory.GetFiles(SampleDir, "*", SearchOption.AllDirectories)
            .Select(f => (prefix + Path.GetRelativePath(SampleDir, f).Replace('\\', '/'), File.ReadAllBytes(f)))
            .Concat(extra).ToArray();

    public static string Manifest(string id = "dev.test.pack", string contents = """{ "themes": ["t.json"] }""", string? extra = null)
        => $$"""{ "id": "{{id}}", "name": "Test Pack", "version": "1.0.0", "type": "content", {{(extra is null ? "" : extra + ",")}} "contents": {{contents}} }""";

    public const string Theme = """{ "id": "night", "name": "Night", "base": "dark", "accent": "#123456", "colors": { "AppMainBackground": "#101820" } }""";
}

/// <summary>plugin.json for "type": "content": strict, and never a way to name code.</summary>
public class ContentPackManifestTests
{
    [Fact]
    public void Parse_ReadsTheContentsSection()
    {
        var m = PluginManifest.Parse(File.ReadAllText(Path.Combine(ContentPackKit.SampleDir, "plugin.json")));
        Assert.True(m.IsContent);
        Assert.Equal(new[] { "themes/dusk.json", "themes/paper.json" }, m.Contents!.Themes);
        Assert.Equal(new[] { "lyrics/karaoke.json" }, m.Contents.LyricsPresets);
        Assert.Equal(new[] { "languages/en-XA.json" }, m.Contents.Languages);
        Assert.Empty(m.Warnings);
    }

    [Theory]
    [InlineData("""{ "themes": ["t.json"] }""", "\"extra\": 1", "Unknown field \"extra\"")]
    [InlineData("""{ "themes": ["t.json"] }""", "\"entry\": \"Pack.dll\"", "carry no code")]
    [InlineData("""{ "themes": ["t.json"] }""", "\"entryType\": \"X.Y\"", "carry no code")]
    [InlineData("""{ "themes": ["t.json"] }""", "\"permissions\": [\"network\"]", "no permissions")]
    [InlineData("""{ "themes": ["t.json"] }""", "\"settings\": [{ \"key\": \"a\", \"type\": \"bool\" }]", "cannot declare")]
    [InlineData("{}", null, "lists no files")]
    [InlineData("[]", null, "must be an object")]
    [InlineData("""{ "shaders": ["s.json"] }""", null, "Unknown kind")]
    [InlineData("""{ "themes": ["../escape.json"] }""", null, "inside the pack folder")]
    [InlineData("""{ "themes": ["/abs.json"] }""", null, "inside the pack folder")]
    [InlineData("""{ "themes": ["C:/win.json"] }""", null, "inside the pack folder")]
    [InlineData("""{ "themes": ["a//b.json"] }""", null, "inside the pack folder")]
    [InlineData("""{ "themes": ["themes/look.axaml"] }""", null, "must be a .json file")]
    [InlineData("""{ "themes": ["bin/Pack.dll"] }""", null, "must be a .json file")]
    [InlineData("""{ "themes": ["t.json", "T.json"] }""", null, "listed twice")]
    [InlineData("""{ "themes": "t.json" }""", null, "array of file paths")]
    public void Parse_RejectsBadContentManifests(string contents, string? extra, string expected)
    {
        var ex = Assert.Throws<PluginManifestException>(() => PluginManifest.Parse(ContentPackKit.Manifest("dev.test.pack", contents, extra)));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Parse_RequiresContents()
    {
        var ex = Assert.Throws<PluginManifestException>(() =>
            PluginManifest.Parse("""{ "id": "dev.x.p", "name": "P", "version": "1.0.0", "type": "content" }"""));
        Assert.Contains("\"contents\" is required", ex.Message);
    }

    [Fact]
    public void DotnetManifest_WithContents_OnlyWarns()
    {
        var m = PluginManifest.Parse(PluginSandbox.Manifest(extra: "\"contents\": { \"themes\": [\"t.json\"] }"));
        Assert.True(m.IsDotnet);
        Assert.Null(m.Contents);
        Assert.Contains(m.Warnings, w => w.Contains("\"contents\""));
    }
}

/// <summary>Which pack translations Loc.T may format: a width or format the English text does not
/// use would let a pack make "{0} themes" build a gigabyte string from a number.</summary>
public class ContentPackPlaceholderTests
{
    [Theory]
    [InlineData("{0} themes", "{0} temas", true)]
    [InlineData("{0} of {1}", "{1} de {0}", true)]
    [InlineData("{0} themes", "{{0:D999999999}} {0}", true)] // escaped: literal text
    [InlineData("{0:N0} songs", "{0:N0} canciones", true)]
    [InlineData("{0} themes", "{0:D999999999} themes", false)]
    [InlineData("{0} themes", "{0,999999} themes", false)]
    [InlineData("{0:N0} songs", "{0:D999999999} canciones", false)]
    [InlineData("By {0}", "by {1}", false)]
    public void PlaceholdersFit_RefusesWidthsAndFormatsTheEnglishLacks(string english, string value, bool fits)
        => Assert.Equal(fits, ContentPackLoader.PlaceholdersFit(english, value));
}

/// <summary>Loading content packs from folders and zips: validation, the catalog, and the
/// guarantee that nothing in a content pack is ever loaded as code.</summary>
[Collection("Localization")] // sample packs carry a language: Loc is process-wide
public class ContentPackHostTests : IDisposable
{
    private readonly PluginSandbox _box = new();

    public void Dispose()
    {
        Loc.Instance.SetOverlays(null);
        _box.Dispose();
    }

    private string WritePack(string folder, string manifest, params (string Name, string Text)[] files)
        => _box.WriteFolder(folder, manifest, files.Select(f => (f.Name, PluginSandbox.Utf8(f.Text))).ToArray());

    [Fact]
    public void SamplePack_LoadsAsData_EvenInRestrictedMode()
    {
        ContentPackKit.CopySample(_box);
        _box.Settings.CommunityPluginsEnabled = false; // restricted: no code runs, content still does
        var host = _box.NewHost();
        host.LoadAll();

        var pack = Assert.Single(host.Plugins);
        Assert.True(pack.IsContentPack);
        Assert.Equal(PluginStatus.Active, pack.Status);
        Assert.True(pack.IsEnabled);
        Assert.True(pack.CanToggle);
        Assert.Equal("", pack.Error);
        Assert.Equal("2 themes · 1 lyrics preset · 1 language", pack.Extensions);

        // No code: no assembly path, no load context, no instance, no approval recorded.
        Assert.Null(pack.AssemblyPath);
        Assert.Null(pack.Context);
        Assert.False(pack.IsRunning);
        Assert.Empty(_box.Settings.PluginPermissionGrants);

        // Even with community plugins on and the pack switched off and on, nothing starts.
        host.SetCommunityPluginsEnabled(true);
        pack = host.Plugins.Single();
        host.SetEnabled(pack, false);
        host.SetEnabled(pack, true);
        Assert.Null(pack.Context);
        Assert.False(pack.IsRunning);
        Assert.Equal(PluginStatus.Active, pack.Status);

        Assert.Equal(new[] { "dusk", "paper" }, host.Content.Themes.Select(t => t.Id));
        Assert.False(host.Content.Themes[0].IsLight);
        Assert.True(host.Content.Themes[1].IsLight);
        Assert.Equal("#FF8A5B", host.Content.Themes[0].Accent);
        Assert.Equal("Karaoke night", Assert.Single(host.Content.LyricsPresets).Name);
        Assert.Equal("en-XA", Assert.Single(host.Content.Languages).Culture);
    }

    [Theory]
    [InlineData("helper.dll")]
    [InlineData("run.exe")]
    [InlineData("Look.axaml")]
    [InlineData("Look.xaml")]
    [InlineData("script.ps1")]
    [InlineData("native/libx.so")]
    [InlineData("tool.sh")]
    public void Pack_WithCodeOrMarkup_IsRefused(string file)
    {
        WritePack("pack", ContentPackKit.Manifest(), ("t.json", ContentPackKit.Theme), (file, "MZ"));
        _box.Settings.CommunityPluginsEnabled = true;
        var host = _box.NewHost();
        host.LoadAll();

        var pack = Assert.Single(host.Plugins);
        Assert.Equal(PluginStatus.Invalid, pack.Status);
        Assert.Contains("not allowed in a content pack", pack.Error);
        Assert.False(pack.CanToggle);
        Assert.Null(pack.AssemblyPath);
        Assert.Null(pack.Context);
        pack.IsEnabled = true; // the switch is disabled; a programmatic flip changes nothing
        Assert.Null(pack.Context);
        Assert.Empty(host.Content.Themes);
        var name = Path.GetFileName(file);
        Assert.DoesNotContain(AssemblyLoadContext.All, c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Pack_WithOversizedFile_IsRefused()
    {
        var dir = WritePack("pack", ContentPackKit.Manifest(), ("t.json", ContentPackKit.Theme));
        File.WriteAllBytes(Path.Combine(dir, "preview.png"), new byte[ContentPackRules.MaxFileBytes + 1]);
        var host = _box.NewHost();
        host.LoadAll();
        Assert.Contains("larger than 2 MB", Assert.Single(host.Plugins).Error);
    }

    [Fact]
    public void Pack_WithTooManyFiles_IsRefused()
    {
        var dir = WritePack("pack", ContentPackKit.Manifest(), ("t.json", ContentPackKit.Theme));
        for (var i = 0; i < ContentPackRules.MaxFiles; i++) File.WriteAllText(Path.Combine(dir, $"n{i}.txt"), "x");
        var host = _box.NewHost();
        host.LoadAll();
        Assert.Contains($"more than {ContentPackRules.MaxFiles} files", Assert.Single(host.Plugins).Error);
    }

    [Fact]
    public void Pack_MissingAListedFile_IsRefused()
    {
        WritePack("pack", ContentPackKit.Manifest(contents: """{ "themes": ["t.json", "gone.json"] }"""), ("t.json", ContentPackKit.Theme));
        var host = _box.NewHost();
        host.LoadAll();
        Assert.Contains("\"gone.json\" is listed", Assert.Single(host.Plugins).Error);
    }

    [Theory]
    [InlineData("""{ "id": "a", "name": "A", "base": "dark", "script": "x" }""", "unknown key \"script\"")]
    [InlineData("""{ "id": "a", "name": "A", "base": "dark", "colors": { "SystemAccentColor": "#FFFFFF" } }""", "not a theme colour")]
    [InlineData("""{ "id": "a", "name": "A", "base": "dark", "colors": { "AppMainBackground": "red" } }""", "#RRGGBB")]
    [InlineData("""{ "id": "a", "name": "A", "base": "sepia" }""", "\"dark\" or \"light\"")]
    [InlineData("""{ "id": "a", "name": "A", "base": "dark", "accent": "#80FF0000" }""", "\"accent\" must be")]
    [InlineData("""{ "id": "Bad Id", "name": "A", "base": "dark" }""", "\"id\"")]
    [InlineData("""[1, 2]""", "must be a JSON object")]
    [InlineData("""{ nope""", "not valid JSON")]
    public void BadThemeFile_RefusesThePack(string theme, string expected)
    {
        WritePack("pack", ContentPackKit.Manifest(), ("t.json", theme));
        var host = _box.NewHost();
        host.LoadAll();
        var pack = Assert.Single(host.Plugins);
        Assert.Equal(PluginStatus.Invalid, pack.Status);
        Assert.Contains(expected, pack.Error);
    }

    [Theory]
    [InlineData("""{ "minLineOpacity": 90 }""", "from 0 to 60")]
    [InlineData("""{ "kawarpBlur": 2.5 }""", "whole number")]
    [InlineData("""{ "flowingBackground": "Plasma" }""", "flowingBackground")]
    [InlineData("""{ "visualizerStyle": "Spiral" }""", "visualizerStyle")]
    [InlineData("""{ "fontSize": 30 }""", "unknown key \"fontSize\"")]
    [InlineData("""{ }""", "sets nothing")]
    public void BadLyricsPreset_RefusesThePack(string settings, string expected)
    {
        WritePack("pack", ContentPackKit.Manifest(contents: """{ "lyricsPresets": ["p.json"] }"""),
            ("p.json", $$"""{ "id": "p", "name": "P", "settings": {{settings}} }"""));
        var host = _box.NewHost();
        host.LoadAll();
        Assert.Contains(expected, Assert.Single(host.Plugins).Error);
    }

    [Fact]
    public void LanguagePack_IgnoresUnknownKeys_AndMismatchedPlaceholders()
    {
        WritePack("pack", ContentPackKit.Manifest(contents: """{ "languages": ["l.json"] }"""),
            ("l.json", """{ "culture": "en-XA", "strings": { "Nav.Home": "H", "Nope.NotAKey": "x", "Plugins.ByPack": "by {3}", "Plugins.Removed": "Gone: {0}" } }"""));
        var host = _box.NewHost();
        host.LoadAll();
        var pack = Assert.Single(host.Plugins);
        Assert.Equal(PluginStatus.Active, pack.Status);
        var lang = Assert.Single(host.Content.Languages);
        Assert.Equal(new[] { "Nav.Home", "Plugins.Removed" }, lang.Strings.Keys.OrderBy(k => k));
        Assert.Equal(2, lang.IgnoredKeys);
        Assert.Contains("2 string(s) ignored", pack.Warnings);
        host.SetEnabled(pack, false); // clear the process-wide overlay again
    }

    [Theory]
    [InlineData("""{ "culture": "zz-NOT-a-culture-at-all", "strings": {} }""", "culture code")]
    [InlineData("""{ "culture": "es", "strings": { "Nav.Home": 5 } }""", "must be text")]
    [InlineData("""{ "culture": "es" }""", "\"strings\"")]
    public void BadLanguageFile_RefusesThePack(string language, string expected)
    {
        WritePack("pack", ContentPackKit.Manifest(contents: """{ "languages": ["l.json"] }"""), ("l.json", language));
        var host = _box.NewHost();
        host.LoadAll();
        Assert.Contains(expected, Assert.Single(host.Plugins).Error);
    }

    [Fact]
    public void Toggle_PublishesAndWithdrawsContent_AndPersistsTheChoice()
    {
        ContentPackKit.CopySample(_box);
        var host = _box.NewHost();
        var changes = 0;
        host.Content.Changed += (_, _) => changes++;
        host.LoadAll();
        Assert.Equal(1, changes);

        host.LoadAll(); // same packs, same files: silent (the active theme is not re-applied)
        Assert.Equal(1, changes);

        var pack = host.Plugins.Single();
        pack.IsEnabled = false; // the Settings switch
        Assert.Equal(PluginStatus.Disabled, pack.Status);
        Assert.Empty(host.Content.Themes);
        Assert.Contains(ContentPackKit.SampleId, _box.Settings.DisabledPlugins);
        Assert.Equal(2, changes);

        pack.IsEnabled = true;
        Assert.Equal(PluginStatus.Active, pack.Status);
        Assert.Equal(2, host.Content.Themes.Count);
        Assert.DoesNotContain(ContentPackKit.SampleId, _box.Settings.DisabledPlugins);
        host.SetEnabled(pack, false);
    }

    [Fact]
    public void PreloadContent_ReadsEnabledPacks_WithoutLoadingPlugins()
    {
        ContentPackKit.CopySample(_box);
        var host = _box.NewHost();
        host.PreloadContent();
        Assert.Empty(host.Plugins);
        Assert.Equal(2, host.Content.Themes.Count);

        _box.Settings.DisabledPlugins.Add(ContentPackKit.SampleId);
        var other = _box.NewHost();
        other.PreloadContent();
        Assert.Empty(other.Content.Themes);
    }

    [Fact]
    public void ZipInstall_OfAContentPack_IsActiveRightAway()
    {
        _box.Settings.CommunityPluginsEnabled = false;
        var host = _box.NewHost();
        host.LoadAll();
        var zip = _box.WriteZip("pack.zip", ContentPackKit.SampleEntries("Sample Pack/"));

        var result = host.InstallPackage(zip, allowUpdate: false);

        Assert.Equal(PluginInstallOutcome.Installed, result.Outcome);
        var pack = result.Plugin!;
        Assert.True(pack.IsContentPack);
        Assert.Equal(PluginStatus.Active, pack.Status);
        Assert.Equal(Path.Combine(_box.PluginsDir, ContentPackKit.SampleId), pack.Directory);
        Assert.True(File.Exists(Path.Combine(pack.Directory, "themes", "dusk.json")));
        Assert.Equal(2, host.Content.Themes.Count);

        Assert.True(host.Remove(pack, deleteData: true));
        Assert.Empty(host.Content.Themes);
        Assert.Empty(host.Content.Languages);
    }

    [Theory]
    [InlineData("payload.dll", "not allowed in a content pack")]
    [InlineData("themes/Look.axaml", "not allowed in a content pack")]
    public void ZipInstall_RefusesContentPacksWithCode(string extra, string expected)
    {
        var host = _box.NewHost();
        host.LoadAll();
        var zip = _box.WriteZip("bad.zip", ContentPackKit.SampleEntries("", (extra, new byte[] { 0x4D, 0x5A })));
        var result = host.InstallPackage(zip, allowUpdate: false);
        Assert.Equal(PluginInstallOutcome.Failed, result.Outcome);
        Assert.Contains(expected, result.Message);
        Assert.False(Directory.Exists(Path.Combine(_box.PluginsDir, ContentPackKit.SampleId)));
    }

    [Fact]
    public void ZipInstall_RefusesAPackMissingAListedFile()
    {
        var host = _box.NewHost();
        host.LoadAll();
        var entries = ContentPackKit.SampleEntries().Where(e => e.Item1 != "themes/paper.json").ToArray();
        var result = host.InstallPackage(_box.WriteZip("partial.zip", entries), allowUpdate: false);
        Assert.Equal(PluginInstallOutcome.Failed, result.Outcome);
        Assert.Contains("themes/paper.json", result.Message);
    }
}

/// <summary>Pack themes: the resource overlay, applying it on the real App, and the fallback
/// to the default theme when the pack goes away.</summary>
[Collection("Localization")]
public class ContentPackThemeTests : IDisposable
{
    private readonly PluginSandbox _box = new();

    public void Dispose()
    {
        Loc.Instance.SetOverlays(null);
        _box.Dispose();
    }

    private static Noctis.App? s_app;

    private static Noctis.App RealApp()
    {
        if (s_app == null)
        {
            var app = new Noctis.App();
            app.Initialize();
            s_app = app;
        }
        return s_app;
    }

    private static Color Resolve(Noctis.App app, string key)
    {
        Assert.True(app.TryGetResource(key, app.RequestedThemeVariant, out var v), $"'{key}' not found");
        return v is Color c ? c : AccentTestHarness.ColorOf(v as IBrush);
    }

    private PluginHost SampleHost()
    {
        ContentPackKit.CopySample(_box);
        var host = _box.NewHost();
        host.LoadAll();
        return host;
    }

    [Fact]
    public void Build_DerivesTheRest_AndLaysThePackColoursOnTop()
    {
        var host = SampleHost();
        var dusk = host.Content.FindTheme(ContentPackKit.SampleId + "/dusk")!;
        var r = PackThemeBuilder.Build(dusk);

        Assert.Equal("Dark", r["__BaseVariant"]);
        Assert.Equal(Color.Parse("#17131F"), ((ISolidColorBrush)r["AppMainBackground"]).Color);
        Assert.Equal(Color.Parse("#17131F"), (Color)r["AppMainBackgroundColor"]);
        Assert.Equal(Color.Parse("#F1ECF7"), ((ISolidColorBrush)r["PrimaryTextBrush"]).Color);
        Assert.Equal(Color.Parse("#F1ECF7"), ((ISolidColorBrush)r["SystemControlForegroundBaseHighBrush"]).Color);
        Assert.Equal(Color.Parse("#1F1929"), (Color)r["IslandBackgroundColor"]);
        // Not set by the pack: derived like a custom theme (chrome = main background).
        Assert.Equal(Color.Parse("#17131F"), ((ISolidColorBrush)r["SystemChromeMediumBrush"]).Color);

        var paper = host.Content.FindTheme(ContentPackKit.SampleId + "/paper")!;
        Assert.Equal("Light", PackThemeBuilder.Build(paper)["__BaseVariant"]);
        host.SetEnabled(host.Plugins.Single(), false);
    }

    [AvaloniaFact]
    public void App_AppliesPackThemes_AndFallsBackToGray_WhenThePackIsGone()
    {
        var host = SampleHost();
        var app = RealApp();
        app.PackThemeResolver = key => host.Content.FindTheme(key);
        try
        {
            app.SetTheme(Noctis.App.PackThemePrefix + ContentPackKit.SampleId + "/dusk");
            Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
            Assert.Equal(Color.Parse("#17131F"), Resolve(app, "AppMainBackground"));

            app.SetTheme(Noctis.App.PackThemePrefix + ContentPackKit.SampleId + "/paper");
            Assert.Equal(ThemeVariant.Light, app.RequestedThemeVariant);
            Assert.Equal(Color.Parse("#F7F3EA"), Resolve(app, "AppMainBackground"));
            Assert.Equal(Color.Parse("#2B2620"), Resolve(app, "PrimaryTextBrush"));

            host.SetEnabled(host.Plugins.Single(), false);
            app.SetTheme(Noctis.App.PackThemePrefix + ContentPackKit.SampleId + "/paper");
            Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
            Assert.Equal(Color.Parse("#252525"), Resolve(app, "AppMainBackground")); // Gray
        }
        finally
        {
            app.PackThemeResolver = null;
            app.SetTheme(Noctis.App.ThemeGray);
        }
    }

    [AvaloniaFact]
    public void Settings_ListsPackThemes_AppliesOne_AndFallsBackWhenSwitchedOff()
    {
        using var persistence = new TestPersistenceService();
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpHistory());
        var themes = new List<string>();
        vm.ThemeChanged += (_, key) => themes.Add(key);
        ContentPackKit.CopySample(_box);
        var host = _box.NewHost();
        vm.Plugins = host;
        host.LoadAll();

        Assert.Equal(new[] { "Dusk", "Paper" }, vm.PackThemes.Select(t => t.Name));
        Assert.All(vm.PackThemes, t => Assert.Equal(Loc.T("Plugins.ByPack", "Sample Content Pack"), t.ByLabel));

        var key = ContentPackKit.SampleId + "/dusk";
        vm.ApplyPackThemeCommand.Execute(key);
        Assert.Equal(Noctis.App.PackThemePrefix + key, themes[^1]);
        Assert.Equal(key, vm.ActivePackThemeKey);
        Assert.True(vm.PackThemes.Single(t => t.Key == key).IsActive);
        Assert.False(vm.IsGrayTheme);
        Assert.Equal("#FF8A5B", vm.ActiveAccentHex); // the theme's accent seeds the picker

        // A built-in pick clears the pack theme.
        vm.SetInkThemeCommand.Execute(null);
        Assert.Null(vm.ActivePackThemeKey);
        Assert.Equal("Ink", themes[^1]);

        vm.ApplyPackThemeCommand.Execute(key);
        host.Plugins.Single().IsEnabled = false; // switch the pack off
        Assert.Null(vm.ActivePackThemeKey);
        Assert.True(vm.IsGrayTheme);
        Assert.Equal("Gray", themes[^1]);
        Assert.Empty(vm.PackThemes);
    }

    [AvaloniaFact]
    public void Settings_AppliesALyricsPreset_LeavingUnnamedSettingsAlone()
    {
        using var persistence = new TestPersistenceService();
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpHistory());
        ContentPackKit.CopySample(_box);
        var host = _box.NewHost();
        vm.Plugins = host;
        host.LoadAll();
        vm.LyricsShowBackgroundVocals = false; // not in the preset: must survive

        var preset = Assert.Single(vm.LyricsPresets);
        Assert.Equal("Karaoke night", preset.Name);
        vm.ApplyLyricsPresetCommand.Execute(preset.Key);

        Assert.Equal(35, vm.LyricsMinLineOpacity);
        Assert.True(vm.LyricsFullScreenFocusEnabled);
        Assert.True(vm.LyricsJoinSplitWords);
        Assert.False(vm.LyricsShowTranslations);
        Assert.True(vm.LyricsShowRomanization);
        Assert.True(vm.LyricsFlowingLightEnabled);
        Assert.Equal("Kawarp", vm.LyricsFlowingStyle);
        Assert.Equal(1.6, vm.LyricsKawarpWarp, 3);
        Assert.Equal(9, vm.LyricsKawarpBlur);
        Assert.True(vm.LyricsVisualizerEnabled);
        Assert.Equal("Mirror", vm.LyricsVisualizerStyle);
        Assert.False(vm.LyricsShowBackgroundVocals);
        Assert.Equal(Loc.T("Settings.LyricsPresets.Applied", "Karaoke night"), vm.LyricsPresetStatus);

        host.SetEnabled(host.Plugins.Single(), false);
        Assert.Empty(vm.LyricsPresets);
    }

    [AvaloniaFact]
    public async Task Settings_InstallsAContentPack_WithoutAnyPrompt()
    {
        using var persistence = new TestPersistenceService();
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpHistory());
        var dialogs = new List<ConfirmationRequest>();
        vm.ShowPluginDialog = r => { dialogs.Add(r); return Task.FromResult(new ConfirmationResult(true, false)); };
        var host = _box.NewHost();
        vm.Plugins = host;
        host.LoadAll();

        await vm.InstallPluginPackageAsync(_box.WriteZip("p.zip", ContentPackKit.SampleEntries()));

        Assert.Empty(dialogs);
        Assert.Equal(Loc.T("Plugins.InstalledContent", "Sample Content Pack", "1.0.0"), vm.PluginsStatus);
        Assert.Equal(2, vm.PackThemes.Count);
        host.SetEnabled(host.Plugins.Single(), false);
    }

    internal sealed class NoOpHistory : IPlayHistoryService
    {
        public IReadOnlyList<Noctis.Models.PlayHistoryEvent> Events => Array.Empty<Noctis.Models.PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Noctis.Models.Track track) { }
        public void RecordSkip(Noctis.Models.Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }
}

/// <summary>Language packs layer over Strings.resx through Loc (process-wide, hence the collection).</summary>
[Collection("Localization")]
public class ContentPackLanguageTests : IDisposable
{
    private readonly PluginSandbox _box = new();

    public ContentPackLanguageTests() => Loc.Instance.SetCulture("en");

    public void Dispose()
    {
        Loc.Instance.SetCulture("en");
        Loc.Instance.SetOverlays(null);
        _box.Dispose();
    }

    [AvaloniaFact]
    public void LanguagePack_OverlaysStrings_FallsBackToEnglish_AndGoesAwayWithThePack()
    {
        ContentPackKit.CopySample(_box);
        var host = _box.NewHost();
        host.LoadAll();

        Assert.Contains("en-XA", Loc.Supported);
        Assert.Equal("en", Loc.Supported[0]);
        Loc.Instance.SetCulture("en-XA");
        Assert.Equal("[Ĥöɱé]", Loc.T("Nav.Home"));
        Assert.Equal("Folders", Loc.T("Nav.Folders"));          // not in the pack: English
        Assert.Equal("[ƀý Sample]", Loc.T("Plugins.ByPack", "Sample"));
        Assert.Equal("Nope.Missing", Loc.T("Nope.Missing"));     // key-on-miss unchanged

        host.SetEnabled(host.Plugins.Single(), false);
        Assert.Equal("Home", Loc.T("Nav.Home"));
        Assert.DoesNotContain("en-XA", Loc.Supported);
    }

    [AvaloniaFact]
    public async Task LanguagePicker_ListsThePackLanguage_AndFallsBackWhenItGoesAway()
    {
        ContentPackKit.CopySample(_box);
        using var persistence = new TestPersistenceService();
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new ContentPackThemeTests.NoOpHistory());
        var host = _box.NewHost();
        vm.Plugins = host;
        await vm.LoadAsync(); // preloads the pack's content
        host.LoadAll();

        var option = Assert.Single(vm.LanguageOptions, o => o.Code == "en-XA");
        Assert.Equal("English (Pseudo) · " + Loc.T("Plugins.ByPack", "Sample Content Pack"), option.Display);

        vm.LanguageChoice = option;
        Assert.Equal("en-XA", Loc.Instance.Culture.Name);
        Assert.Equal("[Ĥöɱé]", Loc.T("Nav.Home"));

        host.SetEnabled(host.Plugins.Single(), false);
        Assert.Equal(Loc.SystemLanguage, vm.LanguageChoice!.Code);
        Assert.DoesNotContain(vm.LanguageOptions, o => o.Code == "en-XA");
        Assert.Equal("Home", Loc.T("Nav.Home"));
    }
}
