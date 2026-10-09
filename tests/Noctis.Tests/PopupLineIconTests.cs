using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Noctis.Controls;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-09: "make sure all of the button icons are synchronized and matching in every
/// section, page, options menu and pop-up — the options menu has an Add to Playlist icon, but
/// the icon at the top-left of that pop-up is completely different." Every pop-up header tile
/// now draws the line icon of the menu row / button that opens it, with Noctis.Controls.LineIcon
/// (vector strokes, the same on Windows, macOS and Linux). These pin the map, that the opener
/// still uses the same key, that the new icons are unique, and that no pop-up draws an icon
/// from a font glyph any more.
/// </summary>
public class PopupLineIconTests
{
    private static readonly XNamespace Av = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Noctis.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repo root from " + AppContext.BaseDirectory);
    }

    private static string Src(params string[] parts) => Path.Combine([RepoRoot(), "src", .. parts]);

    /// <summary>Pop-up → the header's geometry key → the files whose menu row / button opens it
    /// with that same key.</summary>
    private static readonly (string View, string Key, string[] Openers)[] Map =
    [
        ("AddToPlaylistDialog", "MenuLinePlaylistAdd", ["Helpers/TrackContextMenuBuilder.cs", "Helpers/AlbumContextMenuBuilder.cs"]),
        ("AddSongsDialog", "MenuLinePlaylistAdd", ["Helpers/TrackContextMenuBuilder.cs"]),
        ("CreatePlaylistDialog", "LineCreatePlaylist", ["Views/MainWindow.axaml"]),
        ("CreateSmartPlaylistDialog", "MenuLineSmartPlaylist", ["Views/MainWindow.axaml"]),
        ("AudioConverterDialog", "MenuLineConvert", ["Helpers/TrackContextMenuBuilder.cs", "Helpers/AlbumContextMenuBuilder.cs"]),
        ("ReplayGainScannerDialog", "MenuLineReplayGain", ["Helpers/TrackContextMenuBuilder.cs", "Helpers/AlbumContextMenuBuilder.cs"]),
        ("SpectrogramWindow", "MenuLineSpectrogram", ["Helpers/TrackContextMenuBuilder.cs"]),
        ("SendToFolderDialog", "MenuLineSendToFolder", ["Helpers/TrackContextMenuBuilder.cs"]),
        ("LyricsSearchDialog", "MenuLineLyrics", ["Helpers/TrackContextMenuBuilder.cs", "Helpers/AlbumContextMenuBuilder.cs"]),
        ("LyricsBackgroundPickerDialog", "MenuLineVideo", ["Helpers/TrackContextMenuBuilder.cs", "Helpers/AlbumContextMenuBuilder.cs"]),
        ("RemoveFromLibraryDialog", "MenuLineTrash", ["Helpers/TrackContextMenuBuilder.cs", "Helpers/AlbumContextMenuBuilder.cs"]),
        ("LyricShareDialog", "MenuLineCamera", ["Views/PlaybackBarView.axaml"]),
        ("PlaylistImportDialog", "MenuLineImport", ["Views/MainWindow.axaml"]),
        ("YouTubeDownloadDialog", "MenuLineSearch", ["Views/MainWindow.axaml"]),
        ("WrapDialog", "PageLineRecap", ["Views/StatisticsView.axaml"]),
        ("DuplicateFinderDialog", "LineDuplicates", []),
        ("OrganizeFilesDialog", "LineOrganizeFiles", []),
        ("MetadataFinderDialog", "LineFindMetadata", []),
    ];

    public static TheoryData<string, string, string[]> HeaderMap()
    {
        var data = new TheoryData<string, string, string[]>();
        foreach (var (view, key, openers) in Map) data.Add(view, key, openers);
        return data;
    }

    private static readonly string[] IconFiles = ["Icons.axaml", "IconsLine.axaml", "IconsPages.axaml"];

    private static Dictionary<string, string> Geometries()
    {
        var all = new Dictionary<string, string>();
        foreach (var f in IconFiles)
        {
            var path = Src("Noctis.UI", "Assets", f);
            if (!File.Exists(path)) continue;
            foreach (var g in XDocument.Load(path).Descendants(Av + "StreamGeometry"))
                all.TryAdd((string)g.Attribute(X + "Key")!, g.Value.Trim());
        }
        return all;
    }

    [Theory]
    [MemberData(nameof(HeaderMap))]
    public void HeaderTile_DrawsItsOpenersLineIcon(string view, string key, string[] openers)
    {
        var doc = XDocument.Load(Src("Noctis", "Views", view + ".axaml"));
        var header = doc.Descendants().Single(e => (string?)e.Attribute(X + "Name") == "HeaderIcon");
        Assert.Equal("LineIcon", header.Name.LocalName);
        Assert.Matches(@"^\{(Static|Dynamic)Resource " + key + @"\}$", (string?)header.Attribute("Data"));
        Assert.True(Geometries().ContainsKey(key), $"{key} is not defined in {string.Join(", ", IconFiles)}");

        // The tile is the rounded well at the top-left; nothing in it is a PNG mask.
        var tile = header.Ancestors(Av + "Border").First();
        Assert.Equal("56", (string?)tile.Attribute("Width"));
        Assert.DoesNotContain(tile.Descendants(), e => e.Name.LocalName.EndsWith("OpacityMask"));

        foreach (var opener in openers)
        {
            var text = File.ReadAllText(Src(["Noctis", .. opener.Split('/')]));
            Assert.True(Regex.IsMatch(text, $@"(""|Resource ){key}(""|\}})"),
                $"{opener} no longer opens {view} with {key}: change both together");
        }
    }

    [Fact]
    public void NewPopupIcons_AreUniqueAndUsed()
    {
        var all = Geometries();
        var mine = XDocument.Load(Src("Noctis.UI", "Assets", "IconsLine.axaml")).Descendants(Av + "StreamGeometry")
            .Select(g => (Key: (string)g.Attribute(X + "Key")!, Data: g.Value.Trim())).ToList();
        Assert.NotEmpty(mine);
        var sources = Directory.EnumerateFiles(Src("Noctis"), "*.*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".axaml") || p.EndsWith(".cs"))
            .Where(p => !p.Replace('\\', '/').Contains("/obj/") && !p.Replace('\\', '/').Contains("/bin/"))
            .Select(File.ReadAllText).ToList();
        foreach (var (key, data) in mine)
        {
            // One drawing, one meaning: no other icon in the app draws the same strokes.
            Assert.DoesNotContain(all, kv => kv.Key != key && kv.Value == data);
            Assert.True(sources.Any(s => s.Contains(key)), $"{key} is drawn but never used");
        }
    }

    /// <summary>A "✕", "⏱" or "♪" character as an icon is not in the bundled Inter font, so
    /// each OS drew it from whatever fallback it had (a colour emoji on Windows, another font or
    /// a box on Linux/macOS). Pop-ups use vector icons only.</summary>
    [Fact]
    public void Popups_DrawNoIconsFromFontGlyphs()
    {
        var views = Directory.EnumerateFiles(Src("Noctis", "Views"), "*.axaml")
            .Where(p => Regex.IsMatch(Path.GetFileName(p), "(Dialog|Window|Toast)")
                        && !p.EndsWith("MainWindow.axaml") && !p.EndsWith("MiniPlayerWindow.axaml"));
        var offenders = new List<string>();
        foreach (var path in views)
        {
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
                if (Regex.IsMatch(lines[i], @"(Text|Content)=""[^""]*[✕✖✗⏱♪♫★☆🗑]"))
                    offenders.Add($"{Path.GetFileName(path)}:{i + 1}");
        }
        Assert.True(offenders.Count == 0, "font-glyph icons: " + string.Join(", ", offenders));
    }

    /// <summary>The Ctrl+K palette is a pop-up too: its rows show the icon the rest of the app
    /// uses for that place / action, and line icons are drawn as strokes.</summary>
    [Fact]
    public void CommandPalette_UsesTheAppsIcons_AndStrokesLineIcons()
    {
        Assert.True(Noctis.ViewModels.CommandPaletteViewModel.IsLine("MenuLineQueue"));
        Assert.True(Noctis.ViewModels.CommandPaletteViewModel.IsLine("LineCreatePlaylist"));
        Assert.False(Noctis.ViewModels.CommandPaletteViewModel.IsLine("FavoritesIcon"));

        var palette = File.ReadAllText(Src("Noctis", "ViewModels", "CommandPaletteViewModel.cs"));
        Assert.Contains("\"Go to Queue\", \"queue\", \"MenuLineQueue\"", palette);
        Assert.Contains("\"Go to Lyrics\", \"lyrics\", \"MenuLineLyrics\"", palette);
        Assert.Contains("\"Go to Favorites\", \"favorites\", \"FavoritesIcon\"", palette);
        // Lyrics Studio: the sidebar's own glyph (also the Studio picker's header), not Lyrics'.
        Assert.Contains("\"Lyrics Studio…\", \"SidebarLyricsStudioIcon\"", palette);
        Assert.Contains("IconGlyph = \"SidebarLyricsStudioIcon\"", File.ReadAllText(Src("Noctis", "ViewModels", "SidebarViewModel.cs")));

        var row = XDocument.Load(Src("Noctis", "Views", "CommandPaletteDialog.axaml"))
            .Descendants().Single(e => e.Name.LocalName == "LineIcon");
        Assert.Equal("{Binding ShowLineIcon}", (string?)row.Attribute("IsVisible"));
    }

    private static void EnsureIcons()
    {
        var app = Application.Current!;
        if (!app.Resources.TryGetResource("MenuLineClose", null, out _))
            app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        if (!app.Resources.TryGetResource("LineCreatePlaylist", null, out _))
            app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/IconsLine.axaml") });
    }

    [AvaloniaFact]
    public void LineIcon_SizesToItsBox_AndTakesTheTextColour()
    {
        EnsureIcons();
        var icon = new LineIcon { Data = (Geometry)Application.Current!.FindResource("MenuLineTrash")!, HorizontalAlignment = HorizontalAlignment.Left };
        var sized = new LineIcon { Data = icon.Data, Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Left };
        var panel = new StackPanel { Children = { icon, sized } };
        var win = new Window { Width = 200, Height = 200, Content = panel, Foreground = Brushes.Orange };
        win.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new Size(16, 16), icon.Bounds.Size);   // the menus' size by default
            Assert.Equal(new Size(22, 22), sized.Bounds.Size);
            Assert.Same(Brushes.Orange, icon.Foreground);       // inherits like PathIcon
            Assert.Equal(2.25, icon.StrokeThickness);           // 1.5px at 16px, MenuV2's stroke
            Assert.Null(icon.Fill);                             // strokes only
        }
        finally { win.Close(); }
    }

    /// <summary>Real Skia only: every pop-up header icon (and the in-dialog action icons) drawn
    /// in a header tile on the Dark and Light themes, saved as PNGs for a visual check.</summary>
    [AvaloniaFact]
    public void Probe_SavesEveryPopupIcon_DarkAndLight()
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        EnsureIcons();
        var app = Application.Current!;
        if (!app.Resources.TryGetResource("PageLineRecap", null, out _))
            app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/IconsPages.axaml") });
        var keys = Map.Select(r => r.Key).Distinct()
            .Concat(["MenuLineClose", "MenuLineCheck", "MenuLineShuffle", "LinePlus", "LineStopwatch"]).ToList();
        var dir = Path.Combine(Path.GetTempPath(), "noctis-popup-icons");
        Directory.CreateDirectory(dir);
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            var grid = new WrapPanel { Margin = new Thickness(12) };
            foreach (var key in keys)
            {
                var tile = new Border
                {
                    Width = 56, Height = 56, CornerRadius = new CornerRadius(18), Margin = new Thickness(6),
                    Background = new SolidColorBrush(theme == ThemeVariant.Dark ? Color.Parse("#1CFFFFFF") : Color.Parse("#0F000000")),
                };
                var icon = new LineIcon
                {
                    Data = (Geometry)app.FindResource(key)!, Width = 22, Height = 22, Opacity = 0.6,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
                // Set only where the design colours it: a null set would block the inherited colour.
                if (key == "MenuLineTrash") icon.Foreground = new SolidColorBrush(Color.Parse("#E74856"));
                tile.Child = icon;
                ToolTip.SetTip(tile, key);
                grid.Children.Add(tile);
            }
            var win = new Window
            {
                Width = 560, Height = 300, RequestedThemeVariant = theme, Content = grid,
                Background = new SolidColorBrush(theme == ThemeVariant.Dark ? Color.Parse("#141414") : Colors.White),
                Foreground = new SolidColorBrush(theme == ThemeVariant.Dark ? Colors.White : Colors.Black),
            };
            win.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                win.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"popup-icons-{theme}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
            finally { win.Close(); }
        }
    }
}
