using System;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Noctis.Localization;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;
using Track = Noctis.Models.Track;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-27, metadata editor:
/// (1) Plain Lyrics drew its scrollbar inside the rounded lyrics box (the TextBox's own
///     ScrollViewer, inset by the box's padding, on a dark track); Details scrolls the whole
///     page, its bar running down the content pane's right edge. Plain Lyrics now scrolls the
///     page the same way. These pin the bar to the Details bar's spot and keep typing at the
///     bottom of long lyrics in view.
/// (2) The iTunes advisory drop-down shows the app's explicit "E" badge next to "Explicit",
///     in the list and in the closed box, while the item still reads "Explicit".
/// </summary>
public class MetadataWindowLyricsScrollTests
{
    private readonly ITestOutputHelper _o;
    public MetadataWindowLyricsScrollTests(ITestOutputHelper o) => _o = o;

    private static string Plain(int lines = 80)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < lines; i++) sb.Append("She said she leavin' her boyfriend, line ").Append(i + 1).Append('\n');
        return sb.ToString().TrimEnd();
    }

    private static (MetadataViewModel vm, MetadataWindow win) Open(string? lyrics = null)
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var track = new Track
        {
            Id = Guid.NewGuid(), Title = "Boyfriend", Artist = "A", Album = "X", AlbumId = Guid.NewGuid(),
            FilePath = Path.Combine(Path.GetTempPath(), "NoctisTests", "meta-lyrics-scroll.flac"),
            Lyrics = lyrics ?? Plain(),
        };
        var lib = new FakeLibraryService();
        lib.TrackList.Add(track);
        var vm = new MetadataViewModel(track, new SearchPopupsShotsTests.OkTags(), lib, new TestPersistenceService(),
            new FakeAnimatedCoverService(), albumScoped: false, albumTracks: null);
        var win = new MetadataWindow(vm) { RequestedThemeVariant = ThemeVariant.Dark, Width = 1000, Height = 760 };
        win.Show();
        var load = vm.InitializeAsync();
        Assert.True(Pump(() => load.IsCompleted));
        return (vm, win);
    }

    private static bool Pump(Func<bool> until, int budgetMs = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < budgetMs)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            if (until()) return true;
            System.Threading.Thread.Sleep(5);
        }
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return until();
    }

    private static void Settle() => Pump(() => false, 250);

    private static TabItem SelectTab(MetadataWindow win, string locKey)
    {
        var tabs = win.FindControl<TabControl>("MetaTabControl")!;
        var tab = tabs.Items.OfType<TabItem>().First(t => Equals(t.Header, Loc.T(locKey)));
        tabs.SelectedItem = tab;
        Pump(() => false, 300);
        return tab;
    }

    private static ScrollBar? PageBar(TabItem tab)
    {
        var content = (Control)tab.Content!;
        return content.GetSelfAndVisualDescendants().OfType<ScrollBar>()
            .Where(b => b.Orientation == Orientation.Vertical && b.IsEffectivelyVisible && b.Bounds.Height > 0)
            .Where(b => b.FindAncestorOfType<ComboBox>() is null)
            .OrderByDescending(b => b.Bounds.Height)
            .FirstOrDefault();
    }

    private static Rect RectIn(Visual v, Visual to) =>
        new Rect(v.Bounds.Size).TransformToAABB(v.TransformToVisual(to)!.Value);

    private static TextBox LyricsBox(MetadataWindow win) =>
        win.GetVisualDescendants().OfType<TextBox>()
           .Single(t => t.IsEffectivelyVisible && t.Classes.Contains("pill-area"));

    [AvaloniaFact]
    public void PlainLyrics_ScrollbarSitsWhereTheDetailsScrollbarDoes()
    {
        var (_, win) = Open();
        try
        {
            var details = SelectTab(win, "Metadata.Details");
            var dBar = PageBar(details);
            Assert.NotNull(dBar);
            var d = RectIn(dBar!, win);

            var plain = SelectTab(win, "Metadata.PlainLyrics");
            var pBar = PageBar(plain);
            Assert.NotNull(pBar);
            var p = RectIn(pBar!, win);
            _o.WriteLine($"Details bar {d}  Plain Lyrics bar {p}");

            var box = LyricsBox(win);
            Assert.False(pBar!.GetVisualAncestors().Contains(box),
                "Plain Lyrics' scrollbar is still the lyrics box's own (inside the rounded box)");
            Assert.True(Math.Abs(p.Right - d.Right) < 0.5, $"right edge {p.Right} vs Details {d.Right}");
            Assert.True(Math.Abs(p.Width - d.Width) < 0.5, $"width {p.Width} vs Details {d.Width}");
            Assert.True(Math.Abs(p.Top - d.Top) < 0.5, $"top {p.Top} vs Details {d.Top}");
            Assert.True(Math.Abs(p.Bottom - d.Bottom) < 0.5, $"bottom {p.Bottom} vs Details {d.Bottom}");
            // The bar sits outside the rounded box, to its right.
            Assert.True(p.Left >= RectIn(box, win).Right, $"bar {p} overlaps the lyrics box {RectIn(box, win)}");
        }
        finally { win.Close(); Pump(() => !win.IsVisible); }
    }

    [AvaloniaFact]
    public void PlainLyrics_ShortLyricsStillFillThePage()
    {
        var (_, win) = Open("Just one line");
        try
        {
            var plain = SelectTab(win, "Metadata.PlainLyrics");
            Assert.Null(PageBar(plain));
            var box = LyricsBox(win);
            var page = (Control)plain.Content!;
            // Box runs down to the action buttons, which sit at the page's bottom (as before).
            var share = page.GetVisualDescendants().OfType<Button>()
                .Single(b => Equals(b.Content, Loc.T("Metadata.ShareLyrics")));
            var shareRect = RectIn(share, page);
            _o.WriteLine($"page {page.Bounds.Size}  box {RectIn(box, page)}  share {shareRect}");
            Assert.True(page.Bounds.Height - shareRect.Bottom < 20, $"Share sits {page.Bounds.Height - shareRect.Bottom}px above the page bottom");
            Assert.True(RectIn(box, page).Height > 300);
        }
        finally { win.Close(); Pump(() => !win.IsVisible); }
    }

    /// <summary>Caret's rect in the page's viewport coordinates.</summary>
    private static Rect CaretIn(TextBox box, Visual to)
    {
        var tp = box.GetVisualDescendants().OfType<TextPresenter>().First();
        var r = tp.TextLayout.HitTestTextPosition(box.CaretIndex);
        return r.TransformToAABB(tp.TransformToVisual(to)!.Value);
    }

    private static Rect Viewport(ScrollViewer sv)
    {
        var scp = sv.GetVisualDescendants().OfType<ScrollContentPresenter>().First(p => p.TemplatedParent == sv);
        return new Rect(scp.Bounds.Size);
    }

    private static ScrollViewer PageViewer(MetadataWindow win, TextBox box)
    {
        // The nearest ScrollViewer above the box that isn't the box's own.
        return box.GetVisualAncestors().OfType<ScrollViewer>().First(s => s.TemplatedParent != box);
    }

    [AvaloniaFact]
    public void PlainLyrics_TypingAtTheBottomKeepsTheCaretInView()
    {
        var (vm, win) = Open();
        try
        {
            SelectTab(win, "Metadata.PlainLyrics");
            var box = LyricsBox(win);
            var sv = PageViewer(win, box);
            var scp = sv.GetVisualDescendants().OfType<ScrollContentPresenter>().First(p => p.TemplatedParent == sv);

            // Clicking into the lyrics doesn't jump the page (the toolbar stays in view).
            var click = RectIn(box, win).TopLeft + new Point(60, 30);
            win.MouseDown(click, MouseButton.Left);
            win.MouseUp(click, MouseButton.Left);
            Settle();
            Assert.True(box.IsFocused, "click did not focus the lyrics box");
            Assert.Equal(0, sv.Offset.Y);
            box.CaretIndex = 0;
            Settle();

            void AssertCaretVisible(string when)
            {
                var c = CaretIn(box, scp);
                _o.WriteLine($"{when}: caret {box.CaretIndex} at {c}, offset {sv.Offset}, viewport {Viewport(sv)}");
                Assert.True(c.Top >= -0.5 && c.Bottom <= Viewport(sv).Height + 0.5,
                    $"{when}: caret {c} outside the viewport {Viewport(sv)} (offset {sv.Offset})");
            }

            AssertCaretVisible("top");

            // Ctrl+End jumps to the last line: the page follows.
            win.KeyPressQwerty(PhysicalKey.End, RawInputModifiers.Control);
            Settle();
            Assert.Equal(box.Text!.Length, box.CaretIndex);
            AssertCaretVisible("Ctrl+End");

            // Typing new lines at the bottom grows the box; the caret stays in view.
            for (var i = 0; i < 12; i++)
            {
                win.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                win.KeyTextInput("new line " + i);
            }
            Settle();
            Assert.EndsWith("new line 11", vm.Lyrics);
            AssertCaretVisible("typed at the bottom");

            // Ctrl+Home back to the top.
            win.KeyPressQwerty(PhysicalKey.Home, RawInputModifiers.Control);
            Settle();
            Assert.Equal(0, box.CaretIndex);
            AssertCaretVisible("Ctrl+Home");

            // PageDown / PageUp page the lyrics (the box itself has no page keys; the
            // ScrollViewer around it handles them, as the box's own one did before).
            var before = sv.Offset.Y;
            win.KeyPressQwerty(PhysicalKey.PageDown, RawInputModifiers.None);
            Settle();
            _o.WriteLine($"PageDown: offset {before} -> {sv.Offset.Y}, caret {box.CaretIndex}");
            Assert.True(sv.Offset.Y > before + 100, $"PageDown did not page the lyrics ({before} -> {sv.Offset.Y})");
            var paged = sv.Offset.Y;
            win.KeyPressQwerty(PhysicalKey.PageUp, RawInputModifiers.None);
            Settle();
            _o.WriteLine($"PageUp: offset {paged} -> {sv.Offset.Y}");
            Assert.True(sv.Offset.Y < paged - 100, $"PageUp did not page back ({paged} -> {sv.Offset.Y})");

            // The mouse wheel over the box scrolls the page (the box's Disabled inner
            // ScrollViewer must not swallow it).
            var mid = RectIn(scp, win).Center; // on the box (it fills the viewport's middle)
            Assert.True(RectIn(box, win).Contains(mid), "wheel point is not over the box");
            var wheelFrom = sv.Offset.Y;
            win.MouseWheel(mid, new Vector(0, -3));
            Settle();
            _o.WriteLine($"wheel: offset {wheelFrom} -> {sv.Offset.Y}");
            Assert.True(sv.Offset.Y > wheelFrom + 10, $"wheel over the box did not scroll ({wheelFrom} -> {sv.Offset.Y})");
        }
        finally { win.Close(); Pump(() => !win.IsVisible); }
    }

    // ── Explicit badge in the iTunes advisory drop-down ──

    private static ComboBox AdvisoryCombo(MetadataWindow win) =>
        win.GetVisualDescendants().OfType<ComboBox>()
           .Single(c => c.IsEffectivelyVisible && AutomationProperties.GetName(c) == Loc.T("Metadata.ITunesAdvisory"));

    private static bool HasBadge(Visual v) =>
        v.GetVisualDescendants().OfType<Border>()
         .Any(b => b.Classes.Contains("explicit-badge") && b.IsEffectivelyVisible && b.Bounds.Width > 0);

    [AvaloniaFact]
    public void Advisory_ExplicitShowsTheBadge_InListAndClosedBox()
    {
        var (vm, win) = Open();
        try
        {
            SelectTab(win, "Metadata.AdvancedDetails");
            var combo = AdvisoryCombo(win);

            vm.SelectedAdvisory = "None";
            Settle();
            Assert.False(HasBadge(combo), "closed box shows a badge for None");

            combo.IsDropDownOpen = true;
            Assert.True(Pump(() => combo.ContainerFromIndex(2) is { IsEffectivelyVisible: true }));
            Settle();
            foreach (var (i, text) in new[] { (0, "None"), (1, "Explicit"), (2, "Clean") })
            {
                var item = (ComboBoxItem)combo.ContainerFromIndex(i)!;
                Assert.Equal(text == "Explicit", HasBadge(item));
                Assert.Equal(text, ControlAutomationPeer.CreatePeerForElement(item).GetName());
            }
            combo.IsDropDownOpen = false;
            Settle();

            vm.SelectedAdvisory = "Explicit";
            Settle();
            Assert.True(HasBadge(combo), "closed box shows no badge for Explicit");
            vm.SelectedAdvisory = "Clean";
            Settle();
            Assert.False(HasBadge(combo), "closed box shows a badge for Clean");
            Assert.Equal(Loc.T("Metadata.ITunesAdvisory"), ControlAutomationPeer.CreatePeerForElement(combo).GetName());
        }
        finally { win.Close(); Pump(() => !win.IsVisible); }
    }

    /// <summary>Under real Skia (NOCTIS_TEST_SKIA=1) saves Details, Plain Lyrics (top,
    /// bottom, short) and the advisory drop-down (open, and closed on Explicit) to
    /// NOCTIS_SHOTS_DIR (default artifacts/metalyrics/&lt;NOCTIS_SHOTS_TAG or "current"&gt;).</summary>
    [AvaloniaFact]
    public void Probe_SavesShots()
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        var tag = Environment.GetEnvironmentVariable("NOCTIS_SHOTS_TAG") is { Length: > 0 } t ? t : "current";
        var dir = Environment.GetEnvironmentVariable("NOCTIS_SHOTS_DIR") is { Length: > 0 } d
            ? Path.Combine(d, tag)
            : Path.Combine(AppContext.BaseDirectory, "metalyrics", tag);
        Directory.CreateDirectory(dir);
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            void Save(Window w, string name)
            {
                Settle();
                var path = Path.Combine(dir, name + ".png");
                w.CaptureRenderedFrame()!.Save(path, PngBitmapEncoderOptions.Default);
                _o.WriteLine(path);
            }

            var (vm, win) = Open();
            try
            {
                SelectTab(win, "Metadata.Details");
                Save(win, "details");
                var plain = SelectTab(win, "Metadata.PlainLyrics");
                Save(win, "plain-top");
                var box = LyricsBox(win);
                var sv = box.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault(s => s.TemplatedParent != box)
                         is { } outer && outer.Extent.Height > outer.Viewport.Height + 1
                    ? outer
                    : box.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.TemplatedParent == box);
                sv.Offset = new Vector(0, sv.Extent.Height);
                Save(win, "plain-bottom");

                SelectTab(win, "Metadata.AdvancedDetails");
                var combo = AdvisoryCombo(win);
                combo.BringIntoView();
                vm.SelectedAdvisory = "Explicit";
                Save(win, "advisory-closed-explicit");
                combo.IsDropDownOpen = true;
                Pump(() => combo.ContainerFromIndex(2) is { IsEffectivelyVisible: true });
                Save(win, "advisory-open");
                combo.IsDropDownOpen = false;
            }
            finally { win.Close(); Pump(() => !win.IsVisible); }

            var (_, win2) = Open("Just one line\nand another");
            try
            {
                SelectTab(win2, "Metadata.PlainLyrics");
                Save(win2, "plain-short");
            }
            finally { win2.Close(); Pump(() => !win2.IsVisible); }
        });
    }
}
