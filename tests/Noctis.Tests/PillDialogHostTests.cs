using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-08 rounded pill pop-up, Metadata window first: PillDialogHost's template and
/// pill classes resolve, the open animation settles, and every close path (VM
/// CloseRequested, the Cancel button, Esc) animates out and then closes the window exactly
/// once. Also the one-time backdrop blur and, under real Skia, a PNG of the result.
/// </summary>
public class PillDialogHostTests
{
    private readonly ITestOutputHelper _o;
    public PillDialogHostTests(ITestOutputHelper o) => _o = o;

    private const string ShotsDir = @"D:\NoctisLyricsLab\pill-dialog\shots";

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    /// <summary>Runs jobs and render ticks in real time: the transitions run on the render
    /// clock and the deferred close on a DispatcherTimer.</summary>
    private static bool PumpUntil(Func<bool> condition, int budgetMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < budgetMs)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            if (condition()) return true;
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
        return condition();
    }

    private static Track T(string title, int n, Guid albumId) => new()
    {
        Id = Guid.NewGuid(), Title = title, Artist = "Bad Bunny", AlbumArtist = "Bad Bunny",
        Album = "nadie sabe lo que va a pasar mañana", AlbumId = albumId, TrackNumber = n, TrackCount = 3,
        Genre = "Latin", Year = 2023, Duration = TimeSpan.FromSeconds(200), FilePath = "C:/m/" + title + ".flac",
    };

    private static (MetadataViewModel vm, MetadataWindow win, PillDialogHost host) Open(Window? owner = null)
    {
        var albumId = Guid.NewGuid();
        var tracks = new[] { T("NADIE SABE", 1, albumId), T("MONACO", 2, albumId), T("FINA", 3, albumId) }.ToList();
        var lib = new FakeLibraryService();
        lib.TrackList.AddRange(tracks);
        var vm = new MetadataViewModel(tracks[0], new NullMetadataService(), lib, new TestPersistenceService(),
            new FakeAnimatedCoverService(), albumScoped: true, albumTracks: tracks);
        var win = new MetadataWindow(vm) { RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
        if (owner is null) win.Show();
        else _ = win.ShowDialog(owner);
        var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
        return (vm, win, host);
    }

    private static bool CardSettledOpen(PillDialogHost host) =>
        host.Card is { } card && card.Opacity > 0.999 && host.BackdropLayer!.Opacity > 0.999;

    /// <summary>Owner 10-08 "matches the accent color, not gradient": a pill-primary button's
    /// fill is a SolidColorBrush of exactly the AccentColorBrush resource, and nothing inside
    /// the template paints a gradient (or any other fill) over it.</summary>
    internal static void AssertSolidAccent(Button b)
    {
        Assert.Contains("pill-primary", b.Classes);
        Assert.IsAssignableFrom<ISolidColorBrush>(b.Background);
        Assert.Equal(AccentTestHarness.ResourceColor("AccentColorBrush"), AccentTestHarness.ColorOf(b.Background));
        foreach (var border in b.GetVisualDescendants().OfType<Border>())
            Assert.True(border.Name == "PillFill" || border.Background is null
                        || border.Background is ISolidColorBrush { Color.A: 0 },
                $"'{border.Name}' paints {border.Background} over the accent fill");
        Assert.DoesNotContain(b.GetVisualDescendants().OfType<Border>(), x => x.Background is IGradientBrush);
    }

    /// <summary>Owner 10-08: Save follows the user's accent as a solid fill — also after the
    /// accent changes at runtime (the fill is a DynamicResource) — darkens to
    /// AccentColorBrushDark1 on hover, and fades when disabled.</summary>
    [AvaloniaFact]
    public void SaveButton_IsSolidAccent_FollowsAccentChange_AndHoverDarkens()
    {
        EnsureAppStyles();
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var (vm, win, host) = Open();
            try
            {
                Assert.True(PumpUntil(() => CardSettledOpen(host)), "open animation never settled");
                var save = win.GetVisualDescendants().OfType<Button>().Single(b => b.Command == vm.SaveCommand);
                AssertSolidAccent(save);
                Assert.Equal(Color.Parse("#E74856"), AccentTestHarness.ColorOf(save.Background));

                // Hover: the darker accent shade, still solid; back to the accent on leave.
                // (Pseudo-class set directly, as ArtistDetailViewMountTests does: headless pointer
                // moves over the animated card don't reliably raise :pointerover.)
                var pseudo = (IPseudoClasses)save.Classes;
                pseudo.Set(":pointerover", true);
                var dark1 = AccentTestHarness.ResourceColor("AccentColorBrushDark1");
                Assert.NotEqual(Color.Parse("#E74856"), dark1);
                Assert.True(PumpUntil(() => save.Background is ISolidColorBrush s && s.Color == dark1),
                    $"hover fill {save.Background}, expected {dark1}");
                pseudo.Set(":pointerover", false);
                Assert.True(PumpUntil(() => save.Background is ISolidColorBrush s && s.Color == Color.Parse("#E74856")));

                // Disabled stays clearly disabled.
                save.IsEnabled = false;
                PumpUntil(() => false, 50);
                Assert.Equal(0.5, save.Opacity, 3);
                save.IsEnabled = true;

                // Runtime accent change, the way App.SetAccent does it (drop the old overlay,
                // merge the new one): the fill follows.
                var app = Application.Current!;
                app.Resources.MergedDictionaries.Remove(app.Resources.MergedDictionaries[^1]);
                AccentTestHarness.WithAccent("#3B82F6", ThemeVariant.Dark, () =>
                {
                    // The style value (what the app-wide 60 ms Background tween heads to) is the new
                    // accent exactly; the painted brush lands on it (headless back-to-back tweens can
                    // park a few units short, so that one is checked with a small tolerance).
                    var blue = Color.Parse("#3B82F6");
                    Assert.True(PumpUntil(() => save.GetBaseValue(Avalonia.Controls.Primitives.TemplatedControl.BackgroundProperty) is { HasValue: true } v
                                                && v.Value is ISolidColorBrush s && s.Color == blue),
                        $"style fill stayed {save.GetBaseValue(Avalonia.Controls.Primitives.TemplatedControl.BackgroundProperty)}");
                    static bool Near(Color a, Color b) =>
                        Math.Abs(a.R - b.R) <= 12 && Math.Abs(a.G - b.G) <= 12 && Math.Abs(a.B - b.B) <= 12;
                    Assert.True(PumpUntil(() => save.Background is ISolidColorBrush s && Near(s.Color, blue)),
                        $"painted fill stayed {save.Background}");
                    Assert.IsAssignableFrom<ISolidColorBrush>(save.Background);
                    Assert.DoesNotContain(save.GetVisualDescendants().OfType<Border>(), x => x.Background is IGradientBrush);
                });
            }
            finally { win.Close(); PumpUntil(() => !win.IsVisible); }
        });
    }

    [AvaloniaFact]
    public void MetadataWindow_OpensInPillHost_WithResolvedStyles()
    {
        EnsureAppStyles();
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var (vm, win, host) = Open();
            try
            {
                Assert.NotNull(host.Card);
                Assert.NotNull(host.BackdropLayer);
                // Starts hidden, then the open animation brings card and backdrop in.
                Assert.True(PumpUntil(() => CardSettledOpen(host)), "open animation never settled");
                Assert.True(host.IsOpenStarted);
                Assert.Equal(new CornerRadius(30), host.CornerRadius);

                // The card's content (the whole editor) is inside the template and laid out.
                var tabs = win.GetVisualDescendants().OfType<TabControl>().Single();
                Assert.True(tabs.Bounds.Width > 500, $"tab control width {tabs.Bounds.Width}");

                // Filled pill fields: no stale outlined class, the filled tone and the pill
                // radius come from PillDialog.axaml's resources (so they resolved).
                var all = win.GetVisualDescendants().ToList();
                Assert.DoesNotContain(all.OfType<TextBox>(), b => b.Classes.Contains("metadata-info-field"));
                var field = all.OfType<TextBox>().First(b => b.Classes.Contains("pill-field") && b.IsEffectivelyVisible);
                var chrome = field.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_BorderElement");
                _o.WriteLine($"field chrome bg={AccentTestHarness.ColorOf(chrome.Background)} border={chrome.BorderBrush} r={chrome.CornerRadius}");
                Assert.Equal(Color.Parse("#1CFFFFFF"), AccentTestHarness.ColorOf(chrome.Background));
                Assert.Equal(new CornerRadius(999), chrome.CornerRadius);
                Assert.Equal(Colors.Transparent, AccentTestHarness.ColorOf(chrome.BorderBrush));

                var combo = all.OfType<ComboBox>().First(c => c.Classes.Contains("pill-field") && c.IsEffectivelyVisible);
                Assert.Equal(Color.Parse("#1CFFFFFF"), AccentTestHarness.ColorOf(combo.Background));

                // Footer: Save is the solid accent pill (owner 10-08: no gradient); Cancel the
                // quiet pill. (By command: the Find online pill and panel buttons share the classes.)
                var save = all.OfType<Button>().Single(b => b.Classes.Contains("pill-primary") && b.Command == vm.SaveCommand);
                AssertSolidAccent(save);
                var cancel = all.OfType<Button>().Single(b => b.Classes.Contains("pill-secondary") && b.Command == vm.CancelCommand);
                Assert.Equal(Color.Parse("#1CFFFFFF"), AccentTestHarness.ColorOf(cancel.Background));
            }
            finally { win.Close(); PumpUntil(() => !win.IsVisible); }
        });
    }

    [AvaloniaFact]
    public void CloseRequested_AnimatesThenClosesExactlyOnce()
    {
        EnsureAppStyles();
        var (vm, win, host) = Open();
        var closing = 0;
        var closed = 0;
        win.Closed += (_, _) => closed++;
        Assert.True(PumpUntil(() => CardSettledOpen(host)));

        var sw = Stopwatch.StartNew();
        vm.CancelCommand.Execute(null);   // VM CloseRequested → window.Close()
        win.Closing += (_, _) => closing++;
        vm.CancelCommand.Execute(null);   // a second request mid-animation rides along
        win.Close();                      // and so does a direct Close()

        // Still up and animating out, not closed yet.
        Assert.True(win.IsVisible);
        Assert.True(host.IsClosing);
        Assert.Equal(0, closed);
        // The fading card no longer takes clicks: a second Save there would write the tags twice.
        Assert.False(host.Card!.IsHitTestVisible);

        var ok = PumpUntil(() => closed > 0, 2000);
        _o.WriteLine($"after pump: visible={win.IsVisible} closing={host.IsClosing} closed={closed}");
        Assert.True(ok, "window never closed");
        sw.Stop();
        _o.WriteLine($"closed after {sw.ElapsedMilliseconds} ms; closing events after the first: {closing}");
        Assert.True(sw.ElapsedMilliseconds >= 150, $"closed after {sw.ElapsedMilliseconds} ms — the animation was skipped");
        _o.WriteLine($"card opacity at close: {host.Card!.Opacity:0.000}");
        Assert.True(host.Card.Opacity < 0.1, $"card still at {host.Card.Opacity:0.00} when the window closed");

        // Nothing else closes it again.
        PumpUntil(() => false, 300);
        Assert.Equal(1, closed);
        Assert.False(win.IsVisible);
    }

    [AvaloniaFact]
    public void CancelButton_AnimatesThenCloses()
    {
        EnsureAppStyles();
        var (vm, win, host) = Open();
        var closed = 0;
        win.Closed += (_, _) => closed++;
        Assert.True(PumpUntil(() => CardSettledOpen(host)));

        var cancel = win.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("pill-secondary") && b.Command == vm.CancelCommand);
        // A real click (press + release over it), so the button runs its Command.
        var centre = cancel.TranslatePoint(new Point(cancel.Bounds.Width / 2, cancel.Bounds.Height / 2), win)!.Value;
        var sw = Stopwatch.StartNew();
        win.MouseDown(centre, MouseButton.Left);
        win.MouseUp(centre, MouseButton.Left);
        Assert.True(host.IsClosing);
        // (Under real Skia the two input frames can outlast the animation, so the close is
        // checked by elapsed time rather than by "not closed yet".)

        Assert.True(PumpUntil(() => closed > 0, 2000));
        Assert.True(sw.ElapsedMilliseconds >= 150, $"closed after {sw.ElapsedMilliseconds} ms — the animation was skipped");
        PumpUntil(() => false, 250);
        Assert.Equal(1, closed);
    }

    [AvaloniaFact]
    public void Escape_ClosesLikeCancel()
    {
        EnsureAppStyles();
        var (_, win, host) = Open();
        var closed = 0;
        win.Closed += (_, _) => closed++;
        Assert.True(PumpUntil(() => CardSettledOpen(host)));

        win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        _o.WriteLine($"after esc: closing={host.IsClosing} closed={closed}");
        Assert.True(host.IsClosing);
        Assert.True(PumpUntil(() => closed > 0, 2000));
        Assert.Equal(1, closed);
    }

    [AvaloniaFact]
    public void VetoedClose_ComesBackOpen()
    {
        EnsureAppStyles();
        var (vm, win, host) = Open();
        Assert.True(PumpUntil(() => CardSettledOpen(host)));
        // A later Closing handler that refuses the real close (e.g. an unsaved-changes guard).
        var veto = true;
        win.Closing += (_, e) => { if (veto) e.Cancel = true; };

        vm.CancelCommand.Execute(null);
        Assert.True(host.IsClosing);
        Assert.True(PumpUntil(() => !host.IsClosing && CardSettledOpen(host), 2000), "card stayed hidden after a vetoed close");
        Assert.True(host.Card!.IsHitTestVisible, "card stayed click-through after a vetoed close");
        Assert.True(win.IsVisible);

        veto = false;
        vm.CancelCommand.Execute(null);
        Assert.True(PumpUntil(() => !win.IsVisible, 2000));
    }

    [Fact]
    public void BoxBlur_KeepsFlatAreas_SpreadsDetail_AndClampsEdges()
    {
        const int w = 32, h = 16;
        var flat = Enumerable.Repeat((byte)200, w * h * 4).ToArray();
        PillDialogHost.BoxBlur(flat, w, h, 4, 3);
        Assert.All(flat, b => Assert.Equal(200, b));

        // One bright pixel spreads into its neighbours and loses its peak; channels stay apart.
        var px = new byte[w * h * 4];
        var at = (8 * w + 16) * 4;
        px[at] = 255;           // channel 0 only
        PillDialogHost.BoxBlur(px, w, h, 2, 3);
        Assert.True(px[at] < 255 && px[at] > 0);
        Assert.True(px[at + 4] > 0, "neighbour got nothing");
        Assert.Equal(0, px[at + 1]); // channel 1 untouched
    }

    /// <summary>
    /// Owner 10-08: white specks on the pill backdrop. Wherever the owner snapshot has alpha
    /// below 255 the blurred backdrop is see-through, and the dialog is a transparent window:
    /// the real, sharp owner behind it shows through the blur. Flattening onto an opaque base
    /// before the blur closes every hole: no transparent or half-transparent pixel is left,
    /// fully opaque ones keep their colour, and a translucent one is laid over the base.
    /// </summary>
    [Fact]
    public void FlattenOpaque_LeavesNoSeeThroughPixel()
    {
        // BGRA premultiplied: opaque grey, fully transparent, half-transparent white.
        var px = new byte[]
        {
            40, 50, 60, 255,
            0, 0, 0, 0,
            128, 128, 128, 128,
        };
        var baseColor = Color.FromRgb(0x30, 0x20, 0x10);
        PillDialogHost.FlattenOpaque(px, baseColor, rgba: false, premultiplied: true);
        Assert.Equal(new byte[] { 40, 50, 60, 255 }, px[0..4]);
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30, 255 }, px[4..8]);   // the base, in BGRA order
        // White at 50% over the base: 128 + base·(127/255).
        Assert.Equal(new byte[] { 128 + 8, 128 + 16, 128 + 24, 255 }, px[8..12]);

        // RGBA order, unpremultiplied source: the base lands in R first; an unpremultiplied
        // half-transparent white mixes 50/50.
        var rgba = new byte[] { 0, 0, 0, 0, 255, 255, 255, 128 };
        PillDialogHost.FlattenOpaque(rgba, baseColor, rgba: true, premultiplied: false);
        Assert.Equal(new byte[] { 0x30, 0x20, 0x10, 255 }, rgba[0..4]);
        Assert.Equal(new byte[] { 152, 144, 136, 255 }, rgba[4..8]);

        // And the blur keeps it opaque: every alpha byte is still 255 afterwards.
        const int w = 24, h = 12;
        var holes = new byte[w * h * 4];
        for (var i = 0; i < w * h; i += 3) { holes[i * 4] = 200; holes[i * 4 + 3] = 255; }
        PillDialogHost.FlattenOpaque(holes, baseColor, rgba: false, premultiplied: true);
        PillDialogHost.BoxBlur(holes, w, h, 4, 3);
        for (var i = 0; i < w * h; i++) Assert.Equal(255, holes[i * 4 + 3]);
    }

    private static byte[] Pixels(Bitmap bitmap)
    {
        int w = bitmap.PixelSize.Width, h = bitmap.PixelSize.Height;
        var px = new byte[w * h * 4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(px, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), px.Length, w * 4); }
        finally { handle.Free(); }
        return px;
    }

    /// <summary>
    /// Real Skia only (owner 10-08: white specks on the pill backdrop). An owner whose surface
    /// is translucent (Liquid Glass paints AppWindowBackgroundBrush at 35%) with sharp white
    /// icons on it: once the dialog has settled, every pixel of its frame must be opaque, or
    /// the sharp owner shows through the blur on screen.
    /// </summary>
    [AvaloniaFact]
    public void TranslucentOwner_SettledBackdropIsFullyOpaque()
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        EnsureAppStyles();
        var rail = new StackPanel { Spacing = 24, Margin = new Thickness(28, 60, 0, 0) };
        for (var i = 0; i < 6; i++)
            rail.Children.Add(new PathIcon
            {
                Width = 20, Height = 20, Foreground = Brushes.White, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                Data = Geometry.Parse("M0,0 L20,20 M20,0 L0,20 M10,0 L10,20 M0,10 L20,10"),
            });
        rail.Children.Add(new TextBlock { Text = "Library ~~~", FontSize = 16, Foreground = Brushes.White });
        var owner = new Window
        {
            Width = 1100, Height = 820, RequestedThemeVariant = ThemeVariant.Dark,
            Background = new SolidColorBrush(Color.Parse("#0F0F0F"), 0.35),
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
            Content = rail,
        };
        owner.Show();
        PumpUntil(() => false, 100);

        var (_, win, host) = Open(owner);
        try
        {
            Assert.True(PumpUntil(() => CardSettledOpen(host) && host.BackdropBitmap != null, 3000));
            PumpUntil(() => false, 150);

            var backdrop = Pixels(host.BackdropBitmap!);
            var holes = 0;
            for (var i = 3; i < backdrop.Length; i += 4) if (backdrop[i] < 255) holes++;
            var frame = win.CaptureRenderedFrame()!;
            var px = Pixels(frame);
            int see = 0, minA = 255;
            for (var i = 3; i < px.Length; i += 4)
                if (px[i] < 255) { see++; minA = Math.Min(minA, px[i]); }
            _o.WriteLine($"backdrop {host.BackdropBitmap!.PixelSize} alpha<255: {holes}; dialog frame {frame.PixelSize} alpha<255: {see} (min {minA})");
            Assert.Equal(0, holes);
            Assert.Equal(0, see);
        }
        finally
        {
            if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); }
            owner.Close();
        }
    }

    /// <summary>Real Skia only: the dialog over a busy owner, snapshot blurred behind it.</summary>
    [AvaloniaFact]
    public void Probe_SavesThePillDialogOverItsOwner()
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        EnsureAppStyles();
        Directory.CreateDirectory(ShotsDir);
        DebugLogger.IsEnabled = true;
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var stripes = new StackPanel();
            var colors = new[] { "#E74856", "#2D7DD2", "#F4D35E", "#3BB273", "#7B2CBF", "#FF8C42" };
            for (var i = 0; i < 18; i++)
                stripes.Children.Add(new Border
                {
                    Height = 50,
                    Background = new SolidColorBrush(Color.Parse(colors[i % colors.Length])),
                    Child = new TextBlock { Text = $"Library row {i}", FontSize = 22, Margin = new Thickness(24, 8), Foreground = Brushes.White },
                });
            var owner = new Window { Width = 1100, Height = 820, Content = stripes, RequestedThemeVariant = ThemeVariant.Dark };
            owner.Show();
            PumpUntil(() => false, 100);

            var (_, win, host) = Open(owner);
            try
            {
                var ready = PumpUntil(() => CardSettledOpen(host) && host.BackdropBitmap != null, 3000);
                foreach (var entry in DebugLogger.GetEntries(DebugLogger.Category.UI))
                    _o.WriteLine($"log: {entry.Action} {entry.Metadata}");
                Assert.True(ready, $"open={CardSettledOpen(host)} backdrop={host.BackdropBitmap != null}");
                PumpUntil(() => false, 150);
                _o.WriteLine($"backdrop {host.BackdropBitmap!.PixelSize}");
                win.CaptureRenderedFrame()!.Save(Path.Combine(ShotsDir, "01-metadata-pill-dialog.png"), PngBitmapEncoderOptions.Default);
                var tabs = win.GetVisualDescendants().OfType<TabControl>().Single();
                tabs.SelectedItem = tabs.Items.OfType<TabItem>().First(t => Equals(t.Header, "Options"));
                PumpUntil(() => false, 400);
                win.CaptureRenderedFrame()!.Save(Path.Combine(ShotsDir, "02-options-tab.png"), PngBitmapEncoderOptions.Default);

                // The snapshot is freed with the window.
                win.Close();
                Assert.True(PumpUntil(() => !win.IsVisible && host.BackdropBitmap == null, 2000), "backdrop snapshot outlived the window");
            }
            finally
            {
                if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); }
                owner.Close();
            }
        });
    }

    private sealed class NullMetadataService : IMetadataService
    {
        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return null; }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => true;
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => true;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => true;
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields, AdvancedTagIO.AdvancedFields original) => true;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => true;
    }
}
