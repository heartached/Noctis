using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #108: the drag overlay's drop zones (with "Import dropped files" off) — hit-testing,
/// the lit zone, placement clear of the sidebar — and the sidebar's playlist rows as targets
/// for files dragged in from outside the app. With NOCTIS_TEST_SKIA=1 the probe also saves
/// PNGs of each state to look at.
/// </summary>
public class DropOverlayProbeTests
{
    private const string ShotsDir = @"D:\NoctisLyricsLab\issue108\shots";

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static void Pump(int frames = 4)
    {
        for (var i = 0; i < frames; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(16);
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>A stand-in page behind the overlay: window fill, a 60px rail at the sidebar's
    /// spot and a grid of cover-sized tiles, so a shot shows the overlay over real-ish content.</summary>
    private static (Window Win, DropOverlay Overlay) ShowOverlay(string? accent = null)
    {
        EnsureAppStyles();
        var page = new Canvas();
        for (var row = 0; row < 4; row++)
            for (var col = 0; col < 6; col++)
            {
                var tile = new Border
                {
                    Width = 170, Height = 170, CornerRadius = new CornerRadius(8),
                    Background = new SolidColorBrush(Color.FromRgb((byte)(60 + col * 25), (byte)(40 + row * 30), 90)),
                };
                Canvas.SetLeft(tile, 100 + col * 190);
                Canvas.SetTop(tile, 70 + row * 200);
                page.Children.Add(tile);
            }
        var rail = new Border
        {
            Width = 60, Margin = new Thickness(8, 8, 8, 12), CornerRadius = new CornerRadius(20),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Color.Parse("#F0181818")),
        };
        var overlay = new DropOverlay { IsVisible = true, Opacity = 1 };
        var root = new Panel { Children = { page, rail, overlay } };
        root.Bind(Panel.BackgroundProperty, root.GetResourceObservable("AppWindowBackgroundBrush"));
        var win = new Window { Width = 1280, Height = 800, Content = root, RequestedThemeVariant = ThemeVariant.Dark };
        if (accent != null)
        {
            win.Resources["AccentColorBrush"] = new SolidColorBrush(Color.Parse(accent));
            win.Resources["AccentForegroundBrush"] = Brushes.White;
        }
        win.Show();
        Pump();
        return (win, overlay);
    }

    private static Point Centre(Control c, Visual space)
        => c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), space)!.Value;

    private static Func<Visual, Point> At(Window win, Point inWindow) => v => win.TranslatePoint(inWindow, v)!.Value;

    [AvaloniaFact]
    public void Zones_HitTestUnderThePointer_AndCentreOnThePageRightOfTheSidebar()
    {
        var (win, overlay) = ShowOverlay();
        try
        {
            overlay.ShowZones(startsPlayback: true, "hftfviceusgskinconcept", contentLeft: 76);
            Pump();

            var play = overlay.FindControl<Border>("PlayZone")!;
            var library = overlay.FindControl<Border>("LibraryZone")!;
            var playlist = overlay.FindControl<Border>("PlaylistZone")!;
            Assert.Equal(DropZone.Play, overlay.ZoneAt(At(win, Centre(play, win))));
            Assert.Equal(DropZone.Library, overlay.ZoneAt(At(win, Centre(library, win))));
            Assert.Equal(DropZone.NewPlaylist, overlay.ZoneAt(At(win, Centre(playlist, win))));
            Assert.Equal(DropZone.None, overlay.ZoneAt(At(win, new Point(640, 60))));   // above the zones
            Assert.Equal(DropZone.None, overlay.ZoneAt(At(win, new Point(30, 400))));   // over the rail

            // Centred on the page (x = 76 .. 1280), not on the window, so it clears the rail.
            var card = overlay.FindControl<Border>("ZonesCard")!;
            Assert.Equal(76 + (1280 - 76) / 2.0, Centre(card, win).X, 4);
            Assert.Equal("hftfviceusgskinconcept", overlay.FindControl<TextBlock>("PlaylistZoneName")!.Text);

            overlay.Highlight(DropZone.Library);
            Assert.True(library.Classes.Contains("active"));
            Assert.False(play.Classes.Contains("active"));
            Assert.False(playlist.Classes.Contains("active"));
            Assert.Equal(DropZone.Library, overlay.Highlighted);

            // Something playing: the first zone queues instead.
            overlay.ShowZones(startsPlayback: false, "x", contentLeft: 76);
            Assert.Equal("Add to queue", overlay.FindControl<TextBlock>("PlayZoneTitle")!.Text);

            // "Import dropped files" on: the import card, no zones to hit.
            overlay.ShowImport();
            Pump();
            Assert.False(card.IsVisible);
            Assert.True(overlay.FindControl<Border>("ImportCard")!.IsVisible);
            Assert.Equal(DropZone.None, overlay.ZoneAt(At(win, Centre(library, win))));
            Assert.Equal(DropZone.None, overlay.Highlighted);
        }
        finally { win.Close(); }
    }

    /// <summary>Saves each overlay state as a PNG (real Skia rendering only).</summary>
    [AvaloniaFact]
    public void Probe_SavesTheOverlayStates()
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        Directory.CreateDirectory(ShotsDir);

        void Shot(string name, string? accent, Action<DropOverlay> arrange)
        {
            var (win, overlay) = ShowOverlay(accent);
            try
            {
                arrange(overlay);
                Pump(8);
                win.CaptureRenderedFrame()!.Save(Path.Combine(ShotsDir, name + ".png"));
            }
            finally { win.Close(); }
        }

        Shot("01-zones-play-nothing-lit", "#E74856", o => o.ShowZones(true, "hftfviceusgskinconcept", 76));
        Shot("02-zones-queue-library-lit", "#E74856", o => { o.ShowZones(false, "hftfviceusgskinconcept", 76); o.Highlight(DropZone.Library); });
        Shot("03-zones-playlist-lit", "#E74856", o => { o.ShowZones(true, "hftfviceusgskinconcept", 76); o.Highlight(DropZone.NewPlaylist); });
        Shot("04-zones-play-lit-light-accent", "#E8E8E8", o =>
        {
            o.ShowZones(true, "A very long folder name that will not fit on one line", 236);
            o.Resources["AccentForegroundBrush"] = Brushes.Black;
            o.Highlight(DropZone.Play);
        });
        Shot("05-import-card-setting-on", "#E74856", o => o.ShowImport());
    }

    // ── Sidebar playlist rows as targets for outside files ──

    private sealed class PlaylistPersistence : TestPersistenceService
    {
        public static readonly Guid MineId = Guid.NewGuid(), SmartId = Guid.NewGuid(), BoxedId = Guid.NewGuid();
        public override Task<List<Playlist>> LoadPlaylistsAsync() => Task.FromResult(new List<Playlist>
        {
            new() { Id = BoxedId, Name = "Boxed", Folder = "Box" },
            new() { Id = MineId, Name = "Mine" },
            new() { Id = SmartId, Name = "Smart", IsSmartPlaylist = true },
        });
    }

    [AvaloniaFact]
    public async Task SidebarPlaylistRow_TakesOutsideFiles_ButNotASmartPlaylistOrAFolder()
    {
        EnsureAppStyles();
        var vm = new SidebarViewModel(new PlaylistPersistence(), new FakeLibraryService()) { IsExpanded = true };
        await vm.LoadPlaylistsAsync();
        var view = new SidebarView { DataContext = vm };
        var win = new Window { Width = 260, Height = 1400, Content = view };
        win.Show();
        Pump();
        try
        {
            var list = view.FindControl<ListBox>("PlaylistList")!;
            ListBoxItem RowOf(string label) => list.GetRealizedContainers().OfType<ListBoxItem>()
                .Single(c => (c.DataContext as PlaylistNavItem)?.Label == label);

            Assert.Equal(PlaylistPersistence.MineId, view.TrackExternalFileDrop(At(win, Centre(RowOf("Mine"), win))));
            Assert.True(RowOf("Mine").Classes.Contains("drop-target"));

            Assert.Equal(PlaylistPersistence.BoxedId, view.TrackExternalFileDrop(At(win, Centre(RowOf("Boxed"), win))));
            Assert.False(RowOf("Mine").Classes.Contains("drop-target"));

            Assert.Null(view.TrackExternalFileDrop(At(win, Centre(RowOf("Smart"), win))));
            Assert.Null(view.TrackExternalFileDrop(At(win, Centre(RowOf("Box"), win))));
            Assert.DoesNotContain(list.GetRealizedContainers(), c => c.Classes.Contains("drop-target"));

            // Above the list (the nav rows) nothing is a playlist target.
            var listTop = list.TranslatePoint(new Point(0, 0), win)!.Value;
            Assert.Null(view.TrackExternalFileDrop(At(win, new Point(listTop.X + 20, listTop.Y - 10))));

            view.TrackExternalFileDrop(At(win, Centre(RowOf("Mine"), win)));
            view.EndExternalFileDrop();
            Assert.False(RowOf("Mine").Classes.Contains("drop-target"));
        }
        finally { win.Close(); }
    }
}
