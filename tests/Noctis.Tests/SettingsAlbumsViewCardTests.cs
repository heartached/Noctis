using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 11-02: Settings › Player "Albums View" card (Grid / Carousel + the carousel case)
/// rides the same AlbumViewMode / AlbumCarouselStyle settings as the Albums page View menu,
/// so a pick in either place shows up in the other.
/// </summary>
public class SettingsAlbumsViewCardTests
{
    private readonly ITestOutputHelper _o;
    public SettingsAlbumsViewCardTests(ITestOutputHelper o) => _o = o;

    private static (LibraryAlbumsViewModel Albums, SettingsViewModel Settings) Vms()
    {
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var settings = new SettingsViewModel(persistence, lib, new NoOpPlayHistory());
        return (new LibraryAlbumsViewModel(lib, player, new SidebarViewModel(persistence, lib), settings), settings);
    }

    [AvaloniaFact]
    public void CardAndAlbumsViewMenu_ShareOneSetting()
    {
        var (albums, settings) = Vms();
        Assert.True(settings.IsAlbumViewGrid);
        Assert.True(settings.IsAlbumCaseCover);

        // Settings card → Albums page.
        settings.IsAlbumViewCarousel = true;
        Assert.Equal("carousel", settings.AlbumViewMode);
        Assert.True(albums.IsCarouselView);
        Assert.False(settings.IsAlbumViewGrid);
        settings.IsAlbumCaseVinyl = true;
        Assert.Equal(AlbumCarouselStyle.Vinyl, albums.CarouselStyle);
        Assert.False(settings.IsAlbumCaseCover);

        // Albums page View menu → Settings card.
        albums.SetAlbumViewCommand.Execute("grid");
        Assert.True(settings.IsAlbumViewGrid);
        albums.SetCarouselStyleCommand.Execute("JewelCase");
        Assert.True(settings.IsAlbumCaseJewelCase);
        Assert.False(settings.IsAlbumCaseVinyl);

        // A radio button un-checking itself (false) never changes the choice.
        settings.IsAlbumCaseJewelCase = false;
        Assert.Equal("JewelCase", settings.AlbumCarouselStyle);
        settings.IsAlbumViewGrid = false;
        Assert.Equal("grid", settings.AlbumViewMode);
    }

    [AvaloniaFact]
    public void SettingsChanges_RaiseTheCardsBools()
    {
        var (_, settings) = Vms();
        var raised = new List<string?>();
        settings.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        settings.AlbumViewMode = "carousel";
        settings.AlbumCarouselStyle = "Cassette";
        Assert.Contains(nameof(SettingsViewModel.IsAlbumViewGrid), raised);
        Assert.Contains(nameof(SettingsViewModel.IsAlbumViewCarousel), raised);
        Assert.Contains(nameof(SettingsViewModel.IsAlbumCaseCover), raised);
        Assert.Contains(nameof(SettingsViewModel.IsAlbumCaseCassette), raised);
    }

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
        do
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(5);
        } while (sw.ElapsedMilliseconds < ms);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void PlayerTab_CardTilesFollowAndSetTheSetting()
    {
        EnsureAppStyles();
        var (albums, settings) = Vms();
        settings.SelectedSettingsTab = SettingsViewModel.TabPlayer;
        var view = new SettingsView { DataContext = settings };
        var window = new Window { Width = 920, Height = 900, Content = view };
        window.Show();
        try
        {
            Pump(200);
            List<RadioButton> Group(string name) => view.GetVisualDescendants().OfType<RadioButton>()
                .Where(r => r.GroupName == name).ToList();
            var layout = Group("AlbumsView");
            var cases = Group("AlbumCarouselCase");
            Assert.Equal(2, layout.Count);
            Assert.Equal(4, cases.Count);
            Assert.Equal(new bool?[] { true, false }, layout.Select(r => r.IsChecked));
            Assert.Equal(new bool?[] { true, false, false, false }, cases.Select(r => r.IsChecked));

            // The Albums page's View menu moves the tiles.
            albums.SetAlbumViewCommand.Execute("carousel");
            albums.SetCarouselStyleCommand.Execute("Cassette");
            Pump(50);
            Assert.Equal(new bool?[] { false, true }, layout.Select(r => r.IsChecked));
            Assert.Equal(new bool?[] { false, false, false, true }, cases.Select(r => r.IsChecked));

            // Picking a tile moves the Albums page.
            layout[0].IsChecked = true;
            cases[1].IsChecked = true;
            Pump(50);
            Assert.True(albums.IsGridView);
            Assert.Equal(AlbumCarouselStyle.JewelCase, albums.CarouselStyle);
            Assert.All(layout.Concat(cases), r => Assert.False(string.IsNullOrWhiteSpace(
                Avalonia.Automation.AutomationProperties.GetName(r))));

            // Eyeball render (NOCTIS_TEST_SKIA=1 + NOCTIS_TOPBAR_SHOTS=<prefix>): the card alone.
            var prefix = Environment.GetEnvironmentVariable("NOCTIS_TOPBAR_SHOTS");
            if (HeadlessTestApp.RealRendering && !string.IsNullOrEmpty(prefix))
            {
                var card = layout[0].GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("setting-card"));
                card.BringIntoView();
                Pump(400);
                var dir = Environment.GetEnvironmentVariable("NOCTIS_TOPBAR_SHOTS_DIR") ?? Path.Combine(FindRepoRoot(), "renders");
                Directory.CreateDirectory(dir);
                var frame = window.CaptureRenderedFrame()!;
                var origin = card.TranslatePoint(new Point(0, 0), window)!.Value;
                var rect = new PixelRect((int)origin.X, (int)Math.Max(0, origin.Y), (int)card.Bounds.Width,
                    (int)Math.Min(card.Bounds.Height, window.Height - Math.Max(0, origin.Y)));
                var path = Path.Combine(dir, $"{prefix}-settings-albums-view.png");
                using (var cropped = new Avalonia.Media.Imaging.RenderTargetBitmap(rect.Size))
                {
                    using (var ctx = cropped.CreateDrawingContext())
                        ctx.DrawImage(frame, new Rect(rect.X, rect.Y, rect.Width, rect.Height), new Rect(0, 0, rect.Width, rect.Height));
                    cropped.Save(path);
                }
                _o.WriteLine(path);
            }
        }
        finally { window.Close(); }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Noctis.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }
}
