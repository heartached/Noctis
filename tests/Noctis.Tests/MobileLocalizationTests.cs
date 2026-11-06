using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Localization;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The phone app in every language (beta, Russian phone): no English left in the phone's views
/// and view models, every phone key in all eleven Strings files with the right plural forms, the
/// Settings › Language picker, and large titles that shrink rather than slide under their pill.
/// </summary>
[Collection("Localization")] // Loc.Instance is process-wide: these tests must not interleave
public class MobileLocalizationTests
{
    private static readonly string[] Cultures = { "ar", "de", "es", "fr", "ja", "ko", "ru", "tr", "zh-Hans", "zh-Hant" };

    // ── Every user-visible phone string goes through Strings.resx ──

    /// <summary>
    /// Literal text in a phone view's attributes. Allowed: the explicit badge's "E", the step
    /// numbers, separators, example addresses, and the service names and logo letters on
    /// Scrobbling (brands, never translated).
    /// </summary>
    [Fact]
    public void PhoneViews_HaveNoLiteralText()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "Last.fm", "ListenBrainz", "last", "LB", "you@example.com" };
        var attribute = new Regex(@"\b(Text|Content|PlaceholderText|Watermark|AutomationProperties\.Name|ToolTip\.Tip|Header|Title)=""([^""{][^""]*)""");
        var stringFormat = new Regex(@"StringFormat=('([^']*)'|\{\}[^,}""]*)");
        var found = new List<string>();
        foreach (var file in SourceFiles("Noctis.Mobile", "*.axaml"))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match m in attribute.Matches(lines[i]))
                {
                    var text = m.Groups[2].Value;
                    if (Regex.IsMatch(text, "[A-Za-z]{2,}") && !allowed.Contains(text))
                        found.Add($"{Path.GetFileName(file)}:{i + 1} {m.Value}");
                }
                foreach (Match m in stringFormat.Matches(lines[i]))
                    if (Regex.IsMatch(Regex.Replace(m.Groups[1].Value, @"\{[^{}]*\}", string.Empty), "[A-Za-z]{2,}"))
                        found.Add($"{Path.GetFileName(file)}:{i + 1} {m.Value}");
            }
        }
        Assert.True(found.Count == 0, "English in phone views (use {loc:T Mobile.…}):\n" + string.Join("\n", found));
    }

    /// <summary>
    /// English phrases in the phone's C# (view models, views, services, the Android head): a
    /// capitalised run of words or anything with an ellipsis, outside logs and exceptions. The
    /// allowed ones are not UI: library placeholders the data itself holds, codec and product
    /// names, the device name sent to the computer, log labels, an English id, and the server
    /// client's exception texts (the Account page shows its own wording per error kind).
    /// </summary>
    [Fact]
    public void PhoneCode_HasNoLiteralEnglishPhrases()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "Unknown Artist", "Unknown Album", "Unknown Title", "Monkey's Audio", "Android phone",
            "Plum Ink", "Library rows", "Scan on startup", "Remove downloads",
            "Google code scanner (ML Kit, Google Play services)",
            "Voice Recorder",   // a folder name recorders save into (DeviceMusicScan), not UI text
        };
        var literal = new Regex(@"(\$@|@\$|\$|@)?""((?:[^""\\\n]|\\.)*)""");
        var skip = new Regex(@"DebugLog|\bLog\.|nameof\(|Exception\(|throw |\[InlineData");
        var phrase = new Regex(@"^[\s·“”«»(]*[A-Z][a-z’']+(\s+[A-Za-z’'.,&()-]+)+[\s.…?!”»)]*$");
        var found = new List<string>();
        foreach (var file in SourceFiles("Noctis.Mobile", "*.cs").Concat(SourceFiles("Noctis.Android", "*.cs")))
        {
            if (Path.GetFileName(file) == "NoctisServerClient.cs") continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*')) continue;
                var code = lines[i].Split(" // ")[0];
                if (skip.IsMatch(code)) continue;
                foreach (Match m in literal.Matches(code))
                {
                    var text = m.Groups[2].Value;
                    var bare = Regex.Replace(text, @"\{[^{}]*\}", string.Empty);
                    if ((phrase.IsMatch(bare) || bare.Contains('…')) && !allowed.Contains(text))
                        found.Add($"{Path.GetFileName(file)}:{i + 1} {m.Value}");
                }
            }
        }
        Assert.True(found.Count == 0, "English in phone code (use Loc.T / Loc.Plural with a Mobile.… key):\n" + string.Join("\n", found));
    }

    /// <summary>Every Mobile.* key is in all eleven files, with the English placeholders and the
    /// number of plural forms its language has.</summary>
    [Fact]
    public void EveryPhoneKey_IsTranslatedInEveryLanguage()
    {
        var dir = Path.Combine(FindRepoRoot(), "src", "Noctis.UI", "Localization");
        var english = ReadValues(Path.Combine(dir, "Strings.resx")).Where(e => e.Key.StartsWith("Mobile.", StringComparison.Ordinal)).ToList();
        Assert.True(english.Count > 400, $"only {english.Count} Mobile.* keys");
        var forms = new Dictionary<string, int> { ["ar"] = 6, ["de"] = 2, ["es"] = 2, ["fr"] = 2, ["ja"] = 1, ["ko"] = 1, ["ru"] = 3, ["tr"] = 2, ["zh-Hans"] = 1, ["zh-Hant"] = 1 };
        static string Placeholders(string v) => string.Join(" ", Regex.Matches(v, @"\{[^{}]*\}").Select(m => m.Value).Distinct().OrderBy(p => p, StringComparer.Ordinal));
        var problems = new List<string>();
        foreach (var culture in Cultures)
        {
            var values = ReadValues(Path.Combine(dir, $"Strings.{culture}.resx"));
            foreach (var (key, value) in english)
            {
                if (!values.TryGetValue(key, out var translated) || string.IsNullOrWhiteSpace(translated))
                {
                    problems.Add($"{culture} missing {key}");
                    continue;
                }
                if (value.Contains('|'))
                {
                    var parts = translated.Split('|');
                    if (parts.Length != forms[culture]) problems.Add($"{culture} {key}: {parts.Length} plural forms, needs {forms[culture]}");
                    if (parts.Any(p => Placeholders(p) != Placeholders(value.Split('|')[^1]))) problems.Add($"{culture} {key}: placeholders differ in a form");
                }
                else if (Placeholders(translated) != Placeholders(value))
                    problems.Add($"{culture} {key}: [{Placeholders(translated)}] vs [{Placeholders(value)}]");
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    // ── Plurals ──

    [Theory]
    [InlineData("ru", 1, 0)] [InlineData("ru", 2, 1)] [InlineData("ru", 4, 1)] [InlineData("ru", 5, 2)]
    [InlineData("ru", 11, 2)] [InlineData("ru", 12, 2)] [InlineData("ru", 14, 2)] [InlineData("ru", 21, 0)]
    [InlineData("ru", 22, 1)] [InlineData("ru", 25, 2)] [InlineData("ru", 111, 2)] [InlineData("ru", 0, 2)]
    [InlineData("ar", 0, 0)] [InlineData("ar", 1, 1)] [InlineData("ar", 2, 2)] [InlineData("ar", 3, 3)]
    [InlineData("ar", 10, 3)] [InlineData("ar", 11, 4)] [InlineData("ar", 99, 4)] [InlineData("ar", 100, 5)] [InlineData("ar", 103, 3)]
    [InlineData("fr", 0, 0)] [InlineData("fr", 1, 0)] [InlineData("fr", 2, 1)]
    [InlineData("en", 0, 1)] [InlineData("en", 1, 0)] [InlineData("tr", 1, 0)] [InlineData("es", 7, 1)]
    [InlineData("ja", 1, 0)] [InlineData("ko", 5, 0)] [InlineData("zh", 2, 0)]
    public void PluralForm_FollowsTheLanguagesRules(string language, long n, int form) =>
        Assert.Equal(form, Loc.PluralForm(language, n));

    /// <summary>11-05 (full suite): "ferlin · 14208 scrobbles". The count keeps the language's digit
    /// grouping whatever the form writes ({0} or {0:N0}).</summary>
    [AvaloniaFact]
    public void Plural_GroupsTheCountsDigits()
    {
        try
        {
            Loc.Instance.SetCulture("en");
            Assert.Equal("14,208 scrobbles", Loc.Plural("Mobile.Count.Scrobbles", 14208));
            Assert.Equal("1 scrobble", Loc.Plural("Mobile.Count.Scrobbles", 1));
            Assert.Equal("1,234 songs", Loc.Plural("Count.Songs", 1234));
        }
        finally { Loc.Instance.SetCulture("en"); }
    }

    [AvaloniaFact]
    public void Plural_Russian_HasItsThreeForms()
    {
        try
        {
            Loc.Instance.SetCulture("ru");
            Assert.Equal("1 песня", Loc.Plural("Mobile.Count.Songs", 1));
            Assert.Equal("2 песни", Loc.Plural("Mobile.Count.Songs", 2));
            Assert.Equal("5 песен", Loc.Plural("Mobile.Count.Songs", 5));
            Assert.Equal("11 песен", Loc.Plural("Mobile.Count.Songs", 11));
            Assert.Equal("21 песня", Loc.Plural("Mobile.Count.Songs", 21));
            Assert.Equal("44 песни", Loc.Plural("Mobile.Count.Songs", 44));
            // A whole phone culture (ru-RU) reads the ru translation and its rules.
            Loc.Instance.SetCulture("ru-RU");
            Assert.Equal("3 песни", Loc.Plural("Mobile.Count.Songs", 3));

            Loc.Instance.SetCulture("en");
            Assert.Equal("1 song", Loc.Plural("Mobile.Count.Songs", 1));
            Assert.Equal("44 songs", Loc.Plural("Mobile.Count.Songs", 44));
            // A language with no translation (Dutch) reads English, by English rules.
            Loc.Instance.SetCulture("nl");
            Assert.Equal("1 song", Loc.Plural("Mobile.Count.Songs", 1));
            Assert.Equal("2 songs", Loc.Plural("Mobile.Count.Songs", 2));
            Loc.Instance.SetCulture("de");
            Assert.Equal("1 Song", Loc.Plural("Mobile.Count.Songs", 1));
            Assert.Equal("2 Songs", Loc.Plural("Mobile.Count.Songs", 2));
            Loc.Instance.SetCulture("ja");
            Assert.Equal("3曲", Loc.Plural("Mobile.Count.Songs", 3).Replace(" ", string.Empty));
        }
        finally { Loc.Instance.SetCulture("en"); }
    }

    /// <summary>The beta report's strings, on a Russian phone: Home's Pinned and its hint, Library's
    /// Recently Added, the list's Play / Shuffle and count, the Search tab.</summary>
    [AvaloniaFact]
    public void RussianPhone_ShowsTheReportedStringsInRussian()
    {
        try
        {
            Loc.Instance.SetCulture("ru");
            foreach (var key in new[] { "Mobile.Pinned", "Mobile.Downloaded", "Mobile.RecentlyAdded", "Mobile.Play", "Mobile.Shuffle", "Mobile.Search", "Mobile.Home.PinnedHint" })
            {
                var text = Loc.T(key);
                Assert.NotEqual(Loc.English(key), text);
                Assert.Matches("[А-Яа-яЁё]", text);
            }
            Assert.Equal("44 песни", Loc.Plural("Mobile.Count.Songs", 44));
        }
        finally { Loc.Instance.SetCulture("en"); }
    }

    // ── Settings › Language ──

    [AvaloniaFact]
    public async Task LanguagePicker_AppliesAtOnce_RelabelsTheShell_AndPersists()
    {
        using var rig = MobileFixtures.MakeRig();
        rig.Shell.OpenSettingsCommand.Execute(null);
        var page = Assert.IsType<SettingsPageViewModel>(rig.Shell.CurrentPage);
        await page.Loaded;
        Loc.Instance.SetCulture("en");

        // System first, then English and the ten translations, each in its own words.
        var choices = page.LanguageChoices;
        Assert.Equal(Loc.SystemLanguage, choices[0].Code);
        Assert.True(choices[0].IsSelected);
        Assert.Contains(choices, c => c.Code == "ru" && c.Label == "Русский");
        Assert.Contains(choices, c => c.Code == "ar");
        Assert.Equal(12, choices.Count);

        var songsRow = rig.Shell.LibraryRows.Rows.First(r => r.Kind == LibraryRowKind.Songs);
        var relabelled = new List<string?>();
        songsRow.PropertyChanged += (_, e) => relabelled.Add(e.PropertyName);
        var pageChanged = new List<string?>();
        page.PropertyChanged += (_, e) => pageChanged.Add(e.PropertyName);
        Task save;
        try
        {
            page.ChooseLanguageCommand.Execute(choices.First(c => c.Code == "ru"));
            save = page.PendingSave;

            Assert.Equal("ru", Loc.Instance.Culture.Name);
            Assert.Equal("ru", page.Language);
            Assert.Equal("Русский", page.LanguageValue);
            Assert.True(page.LanguageChoices.Single(c => c.Code == "ru").IsSelected);
            // The strings the view models build re-read at once: the Library rows and Settings.
            Assert.Equal("Песни", songsRow.Title);
            Assert.Contains(nameof(LibraryRowItem.Title), relabelled);
            Assert.Contains(string.Empty, pageChanged);
            Assert.Matches("[А-Яа-яЁё]", page.DownloadsValue);
        }
        finally { Loc.Instance.SetCulture("en"); }

        await save;
        var saved = await rig.Persistence.LoadSettingsAsync();
        Assert.Equal("ru", saved.Language);

        // A new Settings sheet shows the saved choice.
        rig.Shell.DismissSheet();
        rig.Shell.OpenSettingsCommand.Execute(null);
        var again = Assert.IsType<SettingsPageViewModel>(rig.Shell.CurrentPage);
        await again.Loaded;
        Assert.Equal("ru", again.Language);
        Assert.Equal("en", Loc.Instance.Culture.Name);   // loading the page shows the choice, it does not re-apply it
    }

    // ── Headers: a long translated title never slides under the header's pill ──

    /// <summary>
    /// A title far longer than any translation (a language pack layer over ru) on Library, Search,
    /// Favorites and Playlists: it shrinks (no smaller than 70 %) and ends before the header's
    /// buttons; Home and the tab bar's labels fit inside their slots.
    /// </summary>
    [AvaloniaFact]
    public void LongTitles_ShrinkAndStopBeforeTheHeaderButtons()
    {
        using var rig = MobileFixtures.MakeRig(tracks: new[] { MobileFixtures.Song("One"), MobileFixtures.Song("Two") });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        const string Long = "Очень-очень длинная медиатека";
        try
        {
            Loc.Instance.SetCulture("ru");
            Loc.Instance.SetOverlays(new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["ru"] = new Dictionary<string, string>
                {
                    ["Tab.Library"] = Long, ["Mobile.Search"] = Long, ["Nav.Favorites"] = Long, ["Nav.Playlists"] = Long, ["Nav.Home"] = Long,
                    ["Mobile.Tab.Playlists"] = "Плейлисты-плейлисты",
                },
            });
            foreach (var tab in new[] { MobileTab.Library, MobileTab.Search, MobileTab.Favorites, MobileTab.Playlists, MobileTab.Home })
            {
                rig.Shell.SelectTabCommand.Execute(tab);
                MobileFixtures.Land(window);
                var fitName = tab switch
                {
                    MobileTab.Library => "LibraryTitleFit",
                    MobileTab.Search => "SearchTitleFit",
                    MobileTab.Favorites => "FavoritesTitleFit",
                    MobileTab.Playlists => "PlaylistsTitleFit",
                    _ => "HomeTitleFit",
                };
                var fit = MobileFixtures.Named<FitWidth>(view, fitName);
                var title = (TextBlock)fit.Child!;
                Assert.Equal(Long, title.Text);
                Assert.True(fit.Scale < 1, $"{tab}: the long title did not shrink");
                Assert.True(fit.Scale >= 0.7 - 1e-9, $"{tab}: shrank below 70 % ({fit.Scale})");
                var titleRight = fit.TranslatePoint(new Point(fit.Bounds.Width, 0), view)!.Value.X;
                var header = (Grid)fit.Parent!;
                var firstButton = header.Children.Where(c => Grid.GetColumn(c) > 0 && c.IsVisible).Min(c => c.TranslatePoint(new Point(0, 0), view)!.Value.X);
                Assert.True(titleRight <= firstButton + 0.5, $"{tab}: title ends at {titleRight}, the header's buttons start at {firstButton}");
            }

            // The tab bar: each label inside its fifth of the capsule.
            var bar = view.FindControl<Grid>("TabBar")!;
            foreach (var button in bar.Children.OfType<Button>())
            {
                var label = button.GetVisualDescendants().OfType<FitWidth>().Single();
                var left = label.TranslatePoint(new Point(0, 0), button)!.Value.X;
                Assert.True(left >= -0.5 && left + label.Bounds.Width <= button.Bounds.Width + 0.5,
                    $"{button.Name}: label {left}..{left + label.Bounds.Width} outside 0..{button.Bounds.Width}");
            }
        }
        finally
        {
            Loc.Instance.SetOverlays(null);
            Loc.Instance.SetCulture("en");
            window.Close();
        }
    }

    /// <summary>The common case: an English title keeps its full 34 px size.</summary>
    [AvaloniaFact]
    public void ShortTitles_KeepTheirFullSize()
    {
        using var rig = MobileFixtures.MakeRig(tracks: new[] { MobileFixtures.Song("One") });
        Loc.Instance.SetCulture("en");
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        try
        {
            rig.Shell.SelectTabCommand.Execute(MobileTab.Home);
            MobileFixtures.Land(window);
            var fit = MobileFixtures.Named<FitWidth>(view, "HomeTitleFit");
            Assert.Equal(1, fit.Scale);
            Assert.Null(fit.Child!.RenderTransform);
        }
        finally { window.Close(); }
    }

    // ── Eyeball probe: the main pages in Russian ──

    /// <summary>Home, Library, Favorites, Search and Settings in Russian to PNGs. Runs only with
    /// NOCTIS_TEST_SKIA=1 and NOCTIS_L10N_SHOTS=&lt;dir&gt;.</summary>
    [AvaloniaFact]
    public async Task Shots_MainPages_InRussian()
    {
        var dir = Environment.GetEnvironmentVariable("NOCTIS_L10N_SHOTS");
        if (!HeadlessTestApp.RealRendering || string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var app = Application.Current!;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        try { FontManager.Current.AddFontCollection(new Avalonia.Fonts.Inter.InterFontCollection()); }
        catch (ArgumentException) { }
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis.Mobile/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis.Mobile/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
        new MobileTheme().Apply(app, "Ink", MobileTheme.DefaultAccent);

        var songs = Enumerable.Range(1, 44).Select(i => MobileFixtures.Song($"Song {i}", i % 3 == 0 ? "Artist B" : "Artist A", daysAgo: i % 5, favourite: i % 2 == 0)).ToArray();
        var albums = new[] { MobileFixtures.MakeAlbum("Album One", "Artist A", songs.Take(20).ToArray()), MobileFixtures.MakeAlbum("Album Two", "Artist B", songs.Skip(20).ToArray()) };
        using var rig = MobileFixtures.MakeRig(songs, albums, seed: p => p.UpdateSettingsAsync(s => s.MusicFolders.Add("content://com.android.externalstorage.documents/tree/primary%3AMusic")));
        var window = MobileFixtures.Mount(rig.Shell, out _, 412, 915);
        try
        {
            Loc.Instance.SetCulture("ru");
            rig.Shell.Relabel();
            foreach (var tab in new[] { MobileTab.Home, MobileTab.Library, MobileTab.Favorites, MobileTab.Search, MobileTab.Playlists })
            {
                rig.Shell.SelectTabCommand.Execute(tab);
                Pump(300);
                window.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"ru-{tab}.png"));
            }
            rig.Shell.OpenSettingsCommand.Execute(null);
            var settings = (SettingsPageViewModel)rig.Shell.CurrentPage!;
            await settings.Loaded;
            Pump(400);
            window.CaptureRenderedFrame()!.Save(Path.Combine(dir, "ru-Settings.png"));
            settings.Pane = SettingsPane.Language;
            Pump(600);
            window.CaptureRenderedFrame()!.Save(Path.Combine(dir, "ru-Settings-Language.png"));
        }
        finally
        {
            Loc.Instance.SetCulture("en");
            window.Close();
        }
    }

    private static void Pump(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
        Motion.FinishAll();
        Dispatcher.UIThread.RunJobs();
    }

    private static IEnumerable<string> SourceFiles(string project, string pattern) =>
        Directory.EnumerateFiles(Path.Combine(FindRepoRoot(), "src", project), pattern, SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    private static Dictionary<string, string> ReadValues(string resxPath)
        => XDocument.Load(resxPath).Root!
            .Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "", StringComparer.Ordinal);

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Noctis.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }
}
