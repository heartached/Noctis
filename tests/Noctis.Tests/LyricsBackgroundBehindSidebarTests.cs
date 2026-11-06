using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #184: Settings › Lyrics › Background Behind the Sidebar. Off (the default) the lyrics
/// page's backdrop starts where the page does, beside the sidebar rail, exactly as before. On,
/// the same backdrop layers reach the window's left edge (under the rail) while the page's
/// content keeps its place, the hover slide never opens a gap, and turning it off or leaving
/// the window edge behind puts everything back.
/// </summary>
public class LyricsBackgroundBehindSidebarTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NoctisTests", "lyrics-bleed-" + Guid.NewGuid().ToString("N"));

    public LyricsBackgroundBehindSidebarTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private sealed class StubLrcLib : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
            => Task.FromResult(new List<LrcLibResult>());
    }

    private sealed class StubNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
    }

    private SettingsViewModel CreateSettings() => new(
        new PersistenceService(_root), new FakeLibraryService(), new NoOpPlayHistoryService());

    private static PlayerViewModel CreatePlayer() => new(
        new FakeAudioPlayer(), new FakeLibraryService(),
        new TestPersistenceService(), new FakeAnimatedCoverService())
    {
        LyricsCenterWhenNoLyrics = false,
    };

    // ── Setting ──────────────────────────────────────────────

    [Fact]
    public void FreshInstall_IsOff()
    {
        Assert.False(new AppSettings().LyricsBackgroundBehindSidebar);
        Assert.False(CreatePlayer().LyricsBackgroundBehindSidebar);
    }

    [AvaloniaFact]
    public async Task Setting_ReachesThePlayer_SurvivesReload_AndResetTurnsItOff()
    {
        var vm = CreateSettings();
        await vm.LoadAsync();
        Assert.False(vm.LyricsBackgroundBehindSidebar);
        var player = CreatePlayer();
        vm.SetPlayer(player);

        vm.LyricsBackgroundBehindSidebar = true;
        Assert.True(player.LyricsBackgroundBehindSidebar);
        await vm.SaveAsync();

        Assert.True((await new PersistenceService(_root).LoadSettingsAsync()).LyricsBackgroundBehindSidebar);
        var reloaded = CreateSettings();
        await reloaded.LoadAsync();
        Assert.True(reloaded.LyricsBackgroundBehindSidebar);

        reloaded.ResetSettingsToDefaults(new AppSettings());
        Assert.False(reloaded.LyricsBackgroundBehindSidebar);
        await reloaded.SaveAsync();
        var afterReset = CreateSettings();
        await afterReset.LoadAsync();
        Assert.False(afterReset.LyricsBackgroundBehindSidebar);
    }

    // ── Lyrics page ──────────────────────────────────────────

    private sealed record Page(Window Window, DockPanel Root, LyricsView View, PlayerViewModel Player);

    /// <summary>The real view in MainWindow's shape: a RootPanel with the rail's 76 px left
    /// margin and a slide TranslateTransform, hosting the page through the view locator.</summary>
    private static Page Mount(bool on, LayoutTransformControl? scaleHost = null)
    {
        var player = CreatePlayer();
        player.LyricsBackgroundBehindSidebar = on;
        player.CurrentTrack = new Track { Title = "Probe", Artist = "Test", Album = "Test" };
        var vm = new LyricsViewModel(player, new StubLrcLib(), new StubNetEase(), new MetadataService(),
            new TestPersistenceService(), new FakeLibraryService())
        {
            ShowLyricsSearchDialog = _ => Task.CompletedTask,
            ExtraLyricsSources = Array.Empty<Services.Lyrics.OnlineLyricsSource>(),
        };

        var host = new ContentControl();
        host.DataTemplates.Add(new CachedViewLocator(new Dictionary<Type, Func<Control>>
        {
            [typeof(LyricsViewModel)] = () => new LyricsView(),
        }));
        var root = new DockPanel
        {
            Margin = new Thickness(76, 0, 0, 0),
            RenderTransform = new TranslateTransform(0, 0),
        };
        root.Children.Add(host);
        var layer = new Panel();
        layer.Children.Add(root);
        Control content = layer;
        if (scaleHost != null)
        {
            scaleHost.Child = layer;
            content = scaleHost;
        }
        var win = new Window { Width = 1600, Height = 900, Content = content };
        win.Show();
        host.Content = vm;
        Pump();
        var view = win.GetVisualDescendants().OfType<LyricsView>().Single();
        return new Page(win, root, view, player);
    }

    private static void Pump()
    {
        for (var i = 0; i < 4; i++) Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Left edge in the coordinates of <paramref name="space"/> (render transforms included).</summary>
    private static double LeftIn(Visual v, Visual space) => v.TranslatePoint(new Point(0, 0), space)!.Value.X;

    private static double RightIn(Visual v, Visual space) =>
        v.TranslatePoint(new Point(v.Bounds.Width, 0), space)!.Value.X;

    private static Control Backdrop(LyricsView view) => view.FindControl<Grid>("LyricsBlurBackdrop")!;
    private static Control Content(LyricsView view) => view.FindControl<Border>("LeftPanel")!;

    [AvaloniaFact]
    public void Off_BackdropStartsBesideTheRail_AsBefore()
    {
        var page = Mount(on: false);
        try
        {
            Assert.Equal(76, LeftIn(Backdrop(page.View), page.Window), 1);
            Assert.Equal(default, Backdrop(page.View).Margin);
            Assert.Null(Backdrop(page.View).RenderTransform);
            Assert.False(page.View.FindControl<Border>("LyricsBackdropFill")!.IsVisible);
        }
        finally { page.Window.Close(); }
    }

    [AvaloniaFact]
    public void On_BackdropReachesTheWindowEdge_ContentStaysPut()
    {
        var off = Mount(on: false);
        double contentX, contentW;
        try
        {
            contentX = LeftIn(Content(off.View), off.Window);
            contentW = Content(off.View).Bounds.Width;
        }
        finally { off.Window.Close(); }

        var page = Mount(on: true);
        try
        {
            var w = page.Window.Bounds.Width;
            foreach (var name in new[] { "LyricsBlurBackdrop", "LyricsBackdropFill" })
            {
                var layer = page.View.FindControl<Control>(name)!;
                Assert.True(layer.IsVisible, name);
                Assert.Equal(0, LeftIn(layer, page.Window), 1);
                Assert.Equal(w, RightIn(layer, page.Window), 1);
            }
            // Every layer, shown or not, carries the same stretch.
            foreach (var name in new[] { "LyricsMediaBackdrop", "MusicVideoBackdropLayer", "KawarpLayer", "PluginLayerHost" })
                Assert.Equal(-76, page.View.FindControl<Control>(name)!.Margin.Left);

            Assert.Equal(contentX, LeftIn(Content(page.View), page.Window), 1);
            Assert.Equal(contentW, Content(page.View).Bounds.Width, 1);
        }
        finally { page.Window.Close(); }
    }

    [AvaloniaFact]
    public void On_HoverSlideAndPinnedRail_KeepTheBackdropOnTheWindowEdge()
    {
        var page = Mount(on: true);
        try
        {
            var backdrop = Backdrop(page.View);
            var w = page.Window.Bounds.Width;

            // Hover-expand slides the page right by 160 px: the backdrop stays on the window.
            ((TranslateTransform)page.Root.RenderTransform!).X = 160;
            Pump();
            Assert.Equal(0, LeftIn(backdrop, page.Window), 1);
            Assert.Equal(w, RightIn(backdrop, page.Window), 1);
            Assert.Equal(76 + 160, LeftIn(Content(page.View), page.Window), 1);

            ((TranslateTransform)page.Root.RenderTransform!).X = 0;
            Pump();
            Assert.Equal(0, LeftIn(backdrop, page.Window), 1);
            Assert.Null(backdrop.RenderTransform);

            // Keep sidebar expanded: the page starts at 236.
            page.Root.Margin = new Thickness(236, 0, 0, 0);
            Pump();
            Assert.Equal(0, LeftIn(backdrop, page.Window), 1);
            Assert.Equal(w, RightIn(backdrop, page.Window), 1);

            // Fullscreen lyrics hide the rail: the page is flush, nothing to stretch.
            page.Root.Margin = default;
            Pump();
            Assert.Equal(default, backdrop.Margin);
            Assert.Equal(0, LeftIn(backdrop, page.Window), 1);
        }
        finally { page.Window.Close(); }
    }

    [AvaloniaFact]
    public void TurningItOff_RestoresTheNormalBackdrop_AtOnce()
    {
        var page = Mount(on: true);
        try
        {
            Assert.Equal(0, LeftIn(Backdrop(page.View), page.Window), 1);
            page.Player.LyricsBackgroundBehindSidebar = false;
            Pump();
            Assert.Equal(76, LeftIn(Backdrop(page.View), page.Window), 1);
            Assert.Equal(default, Backdrop(page.View).Margin);
            Assert.False(page.View.FindControl<Border>("LyricsBackdropFill")!.IsVisible);

            page.Player.LyricsBackgroundBehindSidebar = true;
            Pump();
            Assert.Equal(0, LeftIn(Backdrop(page.View), page.Window), 1);
        }
        finally { page.Window.Close(); }
    }

    /// <summary>The live report (#184): laid out at x=0 but never drawn there, because the
    /// page's hosts (LyricsView, the page ContentControl) are templated controls that clip to
    /// their bounds. The clips come off only while the stretch is on, and come back after.</summary>
    [AvaloniaFact]
    public void ClippingHosts_AreUnclippedWhileOn_AndRestoredOffAndOnLeave()
    {
        var page = Mount(on: false);
        try
        {
            var host = page.View.GetVisualAncestors().OfType<ContentControl>().First();
            // Templated controls clip by default; whatever they start at is what must come back.
            var viewClip = page.View.ClipToBounds;
            var hostClip = host.ClipToBounds;

            page.Player.LyricsBackgroundBehindSidebar = true;
            Pump();
            // Up to the window's scaled content (Interface Size host, which spans the window).
            var clipping = page.View.FindControl<Border>("LyricsBackdropFill")!.GetVisualAncestors()
                .TakeWhile(a => a is not TopLevel and not LayoutTransformControl)
                .Where(a => a.ClipToBounds).Select(a => a.GetType().Name).ToList();
            Assert.True(clipping.Count == 0, "still clipping: " + string.Join(", ", clipping));

            page.Player.LyricsBackgroundBehindSidebar = false;
            Pump();
            Assert.Equal(viewClip, page.View.ClipToBounds);
            Assert.Equal(hostClip, host.ClipToBounds);

            // Leaving the page (another page swapped into the host) restores them too.
            page.Player.LyricsBackgroundBehindSidebar = true;
            Pump();
            Assert.False(host.ClipToBounds);
            host.Content = "another page";
            Pump();
            Assert.Equal(hostClip, host.ClipToBounds);
            Assert.Equal(viewClip, page.View.ClipToBounds);
        }
        finally { page.Window.Close(); }
    }

    [AvaloniaFact]
    public void InterfaceSize_StretchIsInPageUnits_AndStillReachesTheEdge()
    {
        var scale = new LayoutTransformControl { LayoutTransform = new ScaleTransform(1.5, 1.5) };
        var page = Mount(on: true, scale);
        try
        {
            // Margin stays the page's own 76 (scaled with everything else) ...
            Assert.Equal(-76, Backdrop(page.View).Margin.Left);
            // ... and on screen that lands on the window's edge.
            Assert.Equal(0, LeftIn(Backdrop(page.View), page.Window), 1);
            // Nothing between the layers and the scaled content clips them (the scale host
            // itself spans the window, so its own clip is the window's edge).
            Assert.DoesNotContain(Backdrop(page.View).GetVisualAncestors()
                .TakeWhile(a => a is not LayoutTransformControl), a => a.ClipToBounds);
        }
        finally { page.Window.Close(); }
    }

    [AvaloniaFact]
    public void Measure_SumsLayoutOffsets_AndSlidesSeparately()
    {
        var inner = new Border();
        var root = new DockPanel { Margin = new Thickness(40, 0, 0, 0), RenderTransform = new TranslateTransform(25, 0) };
        root.Children.Add(new Border { Margin = new Thickness(10, 0, 0, 0), Child = inner });
        var win = new Window { Width = 800, Height = 600, Content = root };
        win.Show();
        try
        {
            Pump();
            var (left, shift) = BackdropBleed.Measure(inner);
            Assert.Equal(50, left, 1);
            Assert.Equal(25, shift, 1);
        }
        finally { win.Close(); }
    }
}
