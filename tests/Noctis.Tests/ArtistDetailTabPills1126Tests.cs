using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 11-26: the artist page tabs are rounded pills in the "See all" family instead of
/// labels over an accent underline and a hairline. Selected = the solid accent pill See all
/// is; the rest = quiet chip pills.
/// </summary>
public class ArtistDetailTabPills1126Tests
{
    private readonly ITestOutputHelper _o;
    public ArtistDetailTabPills1126Tests(ITestOutputHelper o) => _o = o;

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
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
    }

    private static Album MakeAlbum(string name, string artist, int year, int trackCount)
    {
        var id = Guid.NewGuid();
        var album = new Album { Id = id, Name = name, Artist = artist, Year = year, Tracks = new List<Track>() };
        for (var i = 1; i <= trackCount; i++)
        {
            album.Tracks.Add(new Track
            {
                Id = Guid.NewGuid(), Title = $"{name} {i}", Artist = artist, AlbumArtist = artist, Album = name,
                AlbumId = id, TrackNumber = i, DiscNumber = 1, Year = year, Duration = TimeSpan.FromMinutes(3),
                PlayCount = trackCount - i, Genre = "Alternative",
            });
        }
        album.TrackCount = trackCount;
        return album;
    }

    private static (ArtistDetailView View, Window Win, ArtistDetailViewModel Vm) Mount()
    {
        var lib = new FakeLibraryService();
        ((List<Album>)lib.Albums).AddRange(new[]
        {
            MakeAlbum("Phases", "Chase Atlantic", 2019, 12),
            MakeAlbum("Beauty in Death", "Chase Atlantic", 2021, 10),
            MakeAlbum("Swim", "Chase Atlantic", 2022, 1),
        });
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new ArtistDetailViewModel("Chase Atlantic", lib, player);
        var view = new ArtistDetailView { DataContext = vm };
        var win = new Window
        {
            Width = 1280, Height = 900, Content = view,
            RequestedThemeVariant = ThemeVariant.Dark,
            Background = new SolidColorBrush(Color.Parse("#141414")),
        };
        win.Show();
        Pump(400);
        return (view, win, vm);
    }

    private static List<Button> Tabs(Control view) => view.GetVisualDescendants().OfType<Button>()
        .Where(b => b.Classes.Contains("page-tab") && b.IsVisible).ToList();

    private static Color Fill(Button b) =>
        AccentTestHarness.ColorOf(b.GetVisualDescendants().OfType<ContentPresenter>().First().Background);

    [AvaloniaFact]
    public void Tabs_ArePills_SelectedLooksLikeSeeAll()
    {
        EnsureAppStyles();
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var (view, win, vm) = Mount();
            try
            {
                var tabs = Tabs(view);
                Assert.Equal(5, tabs.Count);
                var seeAll = view.GetVisualDescendants().OfType<Button>()
                    .First(b => b.Classes.Contains("see-all-pill") && b.IsEffectivelyVisible);

                // No underline and no hairline under the strip any more.
                Assert.DoesNotContain(view.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("tab-underline"));
                var strip = tabs[0].GetVisualParent<StackPanel>()!;
                var outer = strip.GetVisualParent<StackPanel>()!;
                Assert.Single(outer.Children);

                var selected = Assert.Single(tabs, t => t.Classes.Contains("selected"));
                Assert.Equal("Overview", Avalonia.Automation.AutomationProperties.GetName(selected));
                Assert.Equal(Fill(seeAll), Fill(selected));
                Assert.Equal(AccentTestHarness.ColorOf(seeAll.Foreground), AccentTestHarness.ColorOf(selected.Foreground));
                Assert.Equal(seeAll.CornerRadius, selected.CornerRadius);
                Assert.Equal(seeAll.Padding, selected.Padding);
                Assert.Equal(seeAll.FontSize, selected.FontSize);
                Assert.Equal(seeAll.Bounds.Height, selected.Bounds.Height, 0.5);

                Assert.True(view.TryFindResource("ChipBackground", view.ActualThemeVariant, out var chipRes));
                var chip = AccentTestHarness.ColorOf(chipRes as IBrush);
                foreach (var tab in tabs.Where(t => t != selected))
                {
                    Assert.DoesNotContain("selected", tab.Classes);
                    Assert.Equal(chip, Fill(tab));
                    Assert.Equal(selected.Bounds.Height, tab.Bounds.Height, 0.5);
                }
                Assert.True(strip.Spacing is > 0 and < 12, $"gap {strip.Spacing}");

                // Picking a tab moves the accent pill (after the 150 ms colour glide).
                var songs = tabs.Single(t => Avalonia.Automation.AutomationProperties.GetName(t) == "Songs");
                songs.Command!.Execute(songs.CommandParameter);
                Pump(400);
                Assert.Contains("selected", songs.Classes);
                Assert.DoesNotContain("selected", selected.Classes);
                Assert.Equal(Fill(seeAll), Fill(songs));
                Assert.Equal(chip, Fill(selected));
                Assert.True(vm.IsTabSongs);
            }
            finally { win.Close(); }
        });
    }

    /// <summary>Picture of the strip next to a See all pill (real rendering only):
    /// NOCTIS_TEST_SKIA=1 and NOCTIS_TAB_SHOTS_DIR set.</summary>
    [AvaloniaFact]
    public void TabStrip_SavePng()
    {
        var dir = Environment.GetEnvironmentVariable("NOCTIS_TAB_SHOTS_DIR");
        if (!HeadlessTestApp.RealRendering || string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        EnsureAppStyles();

        foreach (var (accent, name) in new[] { ("#E74856", "red"), ("#F2F2F2", "white") })
        {
            AccentTestHarness.WithAccent(accent, ThemeVariant.Dark, () =>
            {
                var (view, win, _) = Mount();
                try
                {
                    var tabs = Tabs(view);
                    var seeAll = view.GetVisualDescendants().OfType<Button>()
                        .First(b => b.Classes.Contains("see-all-pill") && b.IsEffectivelyVisible);
                    var top = tabs[0].TranslatePoint(new Point(0, 0), win)!.Value;
                    var sa = seeAll.TranslatePoint(new Point(0, 0), win)!.Value;
                    var full = Path.Combine(dir, $"_tabs-{name}-full.png");
                    win.CaptureRenderedFrame()!.Save(full);
                    Crop(full, Path.Combine(dir, $"tabs-{name}.png"), new SKRectI(0, (int)top.Y - 150, (int)win.Width,
                        (int)(Math.Max(sa.Y + seeAll.Bounds.Height, top.Y + tabs[0].Bounds.Height)) + 60));
                    File.Delete(full);
                    _o.WriteLine(Path.Combine(dir, $"tabs-{name}.png"));
                }
                finally { win.Close(); }
            });
        }
    }

    private static void Crop(string src, string dst, SKRectI rect)
    {
        using var bmp = SKBitmap.Decode(src);
        rect = SKRectI.Intersect(rect, new SKRectI(0, 0, bmp.Width, bmp.Height));
        using var sub = new SKBitmap(rect.Width, rect.Height);
        bmp.ExtractSubset(sub, rect);
        using var img = SKImage.FromBitmap(sub);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.Create(dst);
        data.SaveTo(fs);
    }
}
