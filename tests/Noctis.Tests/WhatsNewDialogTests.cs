using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Settings › About › What's new, headless: the About button opens the in-app dialog (not the
/// browser), the dialog closes on Esc / a backdrop click / Done, Open on GitHub goes through the
/// URL seam, and (with NOCTIS_TEST_SKIA=1) real-rendered pictures of the loaded and unavailable
/// states for the owner.
/// </summary>
public class WhatsNewDialogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", "whatsnew-ui-" + Guid.NewGuid().ToString("N"));

    public WhatsNewDialogTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        // The page line icons (the header's sparkle) are merged at the app level in App.axaml.
        if (!app.Resources.TryGetResource("PageLineSparkle", null, out _))
            app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/IconsPages.axaml") });
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

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

    private static bool CardSettledOpen(PillDialogHost host) =>
        host.Card is { } card && card.Opacity > 0.999 && host.BackdropLayer!.Opacity > 0.999;

    private WhatsNewViewModel LoadedVm(List<string>? opened = null)
    {
        new WhatsNewCache(_root).Save(new WhatsNewCacheEntry
        {
            Tag = "v1.7.2", PublishedAt = new DateTimeOffset(2026, 11, 25, 5, 0, 0, TimeSpan.Zero),
            Body = WhatsNewViewModelTests.Body("v1.7.2"), FetchedUtc = DateTimeOffset.UtcNow,
        });
        var vm = new WhatsNewViewModel("1.7.2", new WhatsNewViewModelTests.FakeSource(), new WhatsNewCache(_root));
        if (opened != null) vm.OpenUrl = opened.Add;
        return vm;
    }

    private static (WhatsNewDialog Win, PillDialogHost Host, Func<int> Closed) Open(WhatsNewViewModel vm)
    {
        var win = new WhatsNewDialog(vm) { RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
        var closed = 0;
        win.Closed += (_, _) => closed++;
        win.Show();
        var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
        Assert.True(PumpUntil(() => CardSettledOpen(host) && vm.IsLoaded), "the dialog never settled open with its notes");
        return (win, host, () => closed);
    }

    /// <summary>A real left click at the middle of <paramref name="control"/>.</summary>
    private static void Click(Control control)
    {
        var window = (Window)TopLevel.GetTopLevel(control)!;
        var at = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task AboutButton_OpensTheDialog_AndClearsTheDot()
    {
        EnsureAppStyles();
        File.WriteAllText(Path.Combine(_root, "settings.json"), "{ \"writtenByVersion\": \"1.7.2\" }");
        var vm = await WhatsNewViewModelTests.LoadSettings(_root, "1.7.3");
        WhatsNewViewModel? shown = null;
        vm.ShowWhatsNewDialog = d => { shown = d; return Task.CompletedTask; };
        vm.SelectedSettingsTab = SettingsViewModel.TabAbout;

        var view = new SettingsView { DataContext = vm };
        var window = new Window { Width = 1100, Height = 1300, Content = view, RequestedThemeVariant = ThemeVariant.Dark };
        window.Show();
        try
        {
            Button? button = null;
            Assert.True(PumpUntil(() => (button = view.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(b => b.Name == "WhatsNewButton" && b.IsEffectivelyVisible)) != null), "no visible What's new button");
            var dot = button!.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Single(e => e.Name == "WhatsNewDot");
            Assert.True(dot.IsVisible);

            button!.BringIntoView();
            PumpUntil(() => false, 200);
            Click(button);
            Assert.True(PumpUntil(() => shown != null));
            Assert.Equal("1.7.3", shown!.Version);
            Assert.False(dot.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Esc_ClosesOnce()
    {
        EnsureAppStyles();
        var (win, host, closed) = Open(LoadedVm());
        win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.True(host.IsClosing);
        Assert.True(PumpUntil(() => closed() > 0, 2000));
        PumpUntil(() => false, 250);
        Assert.Equal(1, closed());
    }

    [AvaloniaFact]
    public void ClickOutsideTheCard_Closes_ClickInside_DoesNot()
    {
        EnsureAppStyles();
        var (win, host, closed) = Open(LoadedVm());
        var card = win.FindControl<DockPanel>("CardRoot")!;
        var inside = card.TranslatePoint(new Point(card.Bounds.Width / 2, 30), win)!.Value;
        win.MouseDown(inside, MouseButton.Left);
        win.MouseUp(inside, MouseButton.Left);
        PumpUntil(() => false, 150);
        Assert.False(host.IsClosing);

        win.MouseDown(new Point(12, 12), MouseButton.Left);
        win.MouseUp(new Point(12, 12), MouseButton.Left);
        Assert.True(PumpUntil(() => closed() == 1, 2000));
    }

    [AvaloniaFact]
    public void Done_Closes_OpenOnGitHub_UsesTheSeam()
    {
        EnsureAppStyles();
        var opened = new List<string>();
        var (win, _, closed) = Open(LoadedVm(opened));

        // The groups: Added, Improved, Fixed cards, one row per change.
        var sections = win.FindControl<ItemsControl>("SectionList")!;
        Assert.Equal(3, sections.ItemCount);

        Click(win.FindControl<Button>("OpenOnGitHubButton")!);
        Assert.Equal(new[] { "https://github.com/heartached/Noctis/releases/tag/v1.7.2" }, opened);
        Assert.Equal(0, closed());

        Click(win.FindControl<Button>("DoneButton")!);
        Assert.True(PumpUntil(() => closed() == 1, 2000));
    }

    // ── Pictures (real rendering only) ─────────────────────────────────

    private const string ShotsDir = "D:/NoctisWT/test-1126/shots-e";

    [AvaloniaFact]
    public void Loaded_And_Unavailable_SavePngs()
    {
        if (!HeadlessTestApp.RealRendering) return;
        Directory.CreateDirectory(ShotsDir);
        EnsureAppStyles();

        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            Shot("whats-new-loaded", LoadedVm(), vm => vm.IsLoaded);

            var empty = new WhatsNewViewModel("1.7.3", new WhatsNewViewModelTests.FakeSource(), new WhatsNewCache(_root));
            Shot("whats-new-unavailable", empty, vm => vm.IsUnavailable);
        });
    }

    private static void Shot(string name, WhatsNewViewModel vm, Func<WhatsNewViewModel, bool> ready)
    {
        var win = new WhatsNewDialog(vm)
        {
            RequestedThemeVariant = ThemeVariant.Dark, Width = 1000, Height = 900,
            Background = new SolidColorBrush(Color.Parse("#1C1C1C")),
        };
        win.Show();
        try
        {
            var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
            Assert.True(PumpUntil(() => CardSettledOpen(host) && ready(vm), 5000));
            PumpUntil(() => false, 600);
            var card = win.FindControl<DockPanel>("CardRoot")!;
            var origin = card.TranslatePoint(new Point(0, 0), win)!.Value;
            var full = Path.Combine(ShotsDir, $"_{name}-full.png");
            win.CaptureRenderedFrame()!.Save(full);
            Crop(full, Path.Combine(ShotsDir, name + ".png"), new SKRectI(
                (int)origin.X - 24, (int)origin.Y - 24,
                (int)(origin.X + card.Bounds.Width) + 24, (int)(origin.Y + card.Bounds.Height) + 24));
            File.Delete(full);
        }
        finally
        {
            win.Close();
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
