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
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Search Lyrics (issue #113's source picker) in the pill dialog (owner 10-08: Search Lyrics in
/// the pill dialog): the Metadata editor's shell and its open/close animation, every close path
/// animating out once, and the picked answer still reaching the lyrics page.
/// </summary>
public class LyricsSearchDialogTests
{
    private readonly ITestOutputHelper _o;
    public LyricsSearchDialogTests(ITestOutputHelper o) => _o = o;

    private const string Elrc = "[00:01.00]<00:01.00>word <00:01.50>timed<00:02.00>";

    private static LrcLibResult Result(string? synced = null, string? plain = null, string track = "Test Song") => new()
    {
        TrackName = track,
        ArtistName = "Test Artist",
        Duration = 200,
        SyncedLyrics = synced,
        PlainLyrics = plain ?? (synced == null ? null : LyricsTextHelper.StripTimestamps(synced)),
    };

    /// <summary>A fake set of sources: LRCLIB couldn't connect, Kugou word-synced, NetEase plain.</summary>
    private static IReadOnlyList<LyricsSourceHit> Hits() => new[]
    {
        new LyricsSourceHit("LRCLIB", null, true),
        new LyricsSourceHit("Kugou", Result(synced: Elrc), false),
        new LyricsSourceHit("NetEase", Result(plain: "plain line one\nplain line two"), false),
    };

    private static LyricsSearchViewModel Vm(
        Func<IReadOnlyList<string>, string, string, CancellationToken, Task<IReadOnlyList<LyricsSourceHit>>>? search = null,
        Action<LrcLibResult, string>? apply = null) =>
        new(new Track { Title = "Test Song", Artist = "Test Artist" }, new[] { "LRCLIB", "Kugou", "NetEase" },
            () => Task.FromResult<IReadOnlyList<string>>(new[] { "LRCLIB", "Kugou", "NetEase" }),
            search ?? ((_, _, _, _) => Task.FromResult(Hits())),
            apply ?? ((_, _) => { }));

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
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

    private static (LyricsSearchViewModel vm, LyricsSearchDialog win, PillDialogHost host) Open(
        LyricsSearchViewModel? vm = null, double height = 820)
    {
        vm ??= Vm();
        var search = vm.SearchAsync();
        PumpUntil(() => search.IsCompleted, 500);
        var win = new LyricsSearchDialog(vm) { RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = height };
        win.Show();
        var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
        return (vm, win, host);
    }

    private static bool CardSettledOpen(PillDialogHost host) =>
        host.Card is { } card && card.Opacity > 0.999 && host.BackdropLayer!.Opacity > 0.999;

    private static void Click(Window win, Control target)
    {
        var centre = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), win)!.Value;
        win.MouseDown(centre, MouseButton.Left);
        win.MouseUp(centre, MouseButton.Left);
    }

    private static Button ButtonFor(Window win, System.Windows.Input.ICommand command) =>
        win.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, command));

    /// <summary>The trigger starts the close: the card animates out while the window stays
    /// up, then the window closes exactly once.</summary>
    private void AssertAnimatedCloseOnce(LyricsSearchDialog win, PillDialogHost host, Action trigger)
    {
        var closed = 0;
        win.Closed += (_, _) => closed++;
        Assert.True(PumpUntil(() => CardSettledOpen(host)), "open animation never settled");

        var sw = Stopwatch.StartNew();
        trigger();
        Assert.True(host.IsClosing, "the close did not go through the host's animation");
        Assert.False(host.Card!.IsHitTestVisible);
        win.Close(); // a second request mid-animation rides along

        Assert.True(PumpUntil(() => closed > 0, 2000), "window never closed");
        _o.WriteLine($"closed after {sw.ElapsedMilliseconds} ms, card opacity {host.Card.Opacity:0.000}");
        Assert.True(sw.ElapsedMilliseconds >= 150, $"closed after {sw.ElapsedMilliseconds} ms: the animation was skipped");
        Assert.True(host.Card.Opacity < 0.1, $"card still at {host.Card.Opacity:0.00} when the window closed");
        PumpUntil(() => false, 250);
        Assert.Equal(1, closed);
        Assert.False(win.IsVisible);
    }

    [AvaloniaFact]
    public void Dialog_OpensInThePillHost_WithTheEditorsLook()
    {
        EnsureAppStyles();
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var (vm, win, host) = Open();
            try
            {
                Assert.True(PumpUntil(() => CardSettledOpen(host)), "open animation never settled");
                Assert.True(host.IsOpenStarted);
                Assert.Equal(new CornerRadius(30), host.CornerRadius);

                var all = win.GetVisualDescendants().ToList();
                // The editor's footer: quiet Cancel pill, accent Use Lyrics pill with its sheen.
                var use = ButtonFor(win, vm.ApplyCommand);
                Assert.Contains("pill-primary", use.Classes);
                Assert.Equal(AccentTestHarness.ResourceColor("AccentButtonBackground"), AccentTestHarness.ColorOf(use.Background));
                Assert.Contains(use.GetVisualDescendants().OfType<Border>(), b => b.Name == "PillSheen");
                var cancel = ButtonFor(win, vm.CloseCommand);
                Assert.Contains("pill-secondary", cancel.Classes);
                Assert.Equal(Color.Parse("#1CFFFFFF"), AccentTestHarness.ColorOf(cancel.Background));
                // Search is the editor's "Find online" pill; no round X any more.
                Assert.Contains("pill-secondary", ButtonFor(win, vm.SearchCommand).Classes);
                Assert.DoesNotContain(all.OfType<Button>(), b => b.Classes.Contains("dialog-close"));
                Assert.Null(win.FindControl<Border>("DialogOverlay"));

                // Filled pill fields, and how Auto picks behind the info icon.
                Assert.Equal(2, all.OfType<TextBox>().Count(t => t.Classes.Contains("pill-field")));
                Assert.Contains("pill-field", all.OfType<ComboBox>().Single().Classes);
                var info = all.OfType<PathIcon>().Single(p => p.Name == "AutoInfo");
                Assert.Equal(Localization.Loc.T("LyricsSearch.Hint"), ToolTip.GetTip(info));

                // Source rows are filled pill wells; the selected one wears the accent ring.
                var list = all.OfType<ListBox>().Single();
                var rows = list.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("ls-row")).ToList();
                Assert.Equal(3, rows.Count);
                Assert.All(rows, r => Assert.Equal(Color.Parse("#1CFFFFFF"), AccentTestHarness.ColorOf(r.Background)));
                Assert.Equal("Kugou", vm.SelectedResult?.Source);
                var selected = rows.Single(r => ReferenceEquals(r.DataContext, vm.SelectedResult));
                Assert.Equal(AccentTestHarness.ResourceColor("AccentColorBrush"), AccentTestHarness.ColorOf(selected.BorderBrush));
                Assert.All(rows.Where(r => r != selected), r => Assert.Equal(Colors.Transparent, AccentTestHarness.ColorOf(r.BorderBrush)));
                Assert.Contains(all.OfType<SelectableTextBlock>(), t => t.Text?.Contains("word timed") == true);
                Assert.InRange(host.Card!.Bounds.Height, 559.5, 560.5);
            }
            finally { win.Close(); PumpUntil(() => !win.IsVisible); }
        });
    }

    [AvaloniaFact]
    public void ShortWindow_CapsTheCard_InsteadOfCuttingItOff()
    {
        EnsureAppStyles();
        var (_, win, host) = Open(height: 500);
        try
        {
            Assert.True(PumpUntil(() => CardSettledOpen(host)));
            _o.WriteLine($"card {host.Card!.Bounds}");
            Assert.True(host.Card!.Bounds.Height <= 500 - 47.5, $"card {host.Card.Bounds.Height} tall in a 500 px window");
            Assert.True(host.Card.Bounds.Top >= 0);
        }
        finally { win.Close(); PumpUntil(() => !win.IsVisible); }
    }

    [AvaloniaFact]
    public void CancelButton_AnimatesThenClosesOnce()
    {
        EnsureAppStyles();
        var (vm, win, host) = Open();
        AssertAnimatedCloseOnce(win, host, () => Click(win, ButtonFor(win, vm.CloseCommand)));
    }

    [AvaloniaFact]
    public void Escape_AnimatesThenClosesOnce()
    {
        EnsureAppStyles();
        var (_, win, host) = Open();
        AssertAnimatedCloseOnce(win, host, () => win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None));
    }

    [AvaloniaFact]
    public void ViewModelClose_AnimatesThenClosesOnce()
    {
        EnsureAppStyles();
        var (vm, win, host) = Open();
        AssertAnimatedCloseOnce(win, host, () => vm.CloseCommand.Execute(null));
    }

    [AvaloniaFact]
    public void DirectClose_AnimatesThenClosesOnce()
    {
        EnsureAppStyles();
        var (_, win, host) = Open();
        // Window.Close() is what Alt+F4 comes down to.
        AssertAnimatedCloseOnce(win, host, () => win.Close());
    }

    /// <summary>The pick reaches the caller through the apply callback before the (deferred)
    /// close, once, even if Use Lyrics is clicked again while the card animates out.</summary>
    [AvaloniaFact]
    public void UseLyrics_HandsThePickToTheCallerOnce_ThenAnimatesClosed()
    {
        EnsureAppStyles();
        var applied = new List<(LrcLibResult Result, string Source)>();
        var (vm, win, host) = Open(Vm(apply: (r, s) => applied.Add((r, s))));
        var use = ButtonFor(win, vm.ApplyCommand);
        AssertAnimatedCloseOnce(win, host, () =>
        {
            Click(win, use);
            Assert.Single(applied); // already handed over; the close is still animating
            Click(win, use);
            win.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        });
        var pick = Assert.Single(applied);
        Assert.Equal("Kugou", pick.Source);
        Assert.Equal(Elrc, pick.Result.SyncedLyrics);
    }

    [AvaloniaFact]
    public void PickingAnotherRow_PreviewsIt_AndUseLyricsAppliesThatOne()
    {
        EnsureAppStyles();
        (LrcLibResult Result, string Source)? applied = null;
        var (vm, win, host) = Open(Vm(apply: (r, s) => applied = (r, s)));
        try
        {
            Assert.True(PumpUntil(() => CardSettledOpen(host)));
            var list = win.GetVisualDescendants().OfType<ListBox>().Single();
            var netEase = list.GetVisualDescendants().OfType<Border>()
                .Single(b => b.Classes.Contains("ls-row") && b.DataContext is LyricsSearchResultRow { Source: "NetEase" });
            Click(win, netEase);
            PumpUntil(() => false, 50);
            Assert.Equal("NetEase", vm.SelectedResult?.Source);
            Assert.Contains(win.GetVisualDescendants().OfType<SelectableTextBlock>(), t => t.Text?.Contains("plain line two") == true);

            // A row that found nothing can be looked at but not used.
            var lrclib = list.GetVisualDescendants().OfType<Border>()
                .Single(b => b.Classes.Contains("ls-row") && b.DataContext is LyricsSearchResultRow { Source: "LRCLIB" });
            Click(win, lrclib);
            PumpUntil(() => false, 50);
            Assert.False(ButtonFor(win, vm.ApplyCommand).IsEffectivelyEnabled);

            Click(win, netEase);
            PumpUntil(() => false, 50);
            Click(win, ButtonFor(win, vm.ApplyCommand));
            Assert.Equal("NetEase", applied?.Source);
        }
        finally { if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); } }
    }

    [AvaloniaFact]
    public void EditedQuery_SearchesOnEnter_AndPickingASourceSearchesOnlyIt()
    {
        EnsureAppStyles();
        var asks = new List<(string Sources, string Artist, string Title)>();
        var (vm, win, host) = Open(Vm((sources, artist, title, _) =>
        {
            asks.Add((string.Join(",", sources), artist, title));
            return Task.FromResult(Hits());
        }));
        try
        {
            Assert.True(PumpUntil(() => CardSettledOpen(host)));
            Assert.Equal(("LRCLIB,Kugou,NetEase", "Test Artist", "Test Song"), Assert.Single(asks));

            var boxes = win.GetVisualDescendants().OfType<TextBox>().Where(t => t.Classes.Contains("pill-field")).ToList();
            boxes[1].Focus();
            boxes[1].Text = "  Other Song ";
            PumpUntil(() => false, 30);
            win.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Assert.True(PumpUntil(() => asks.Count == 2));
            Assert.Equal(("LRCLIB,Kugou,NetEase", "Test Artist", "Other Song"), asks[1]);
            Assert.False(host.IsClosing); // Enter searches; it doesn't use or close

            win.GetVisualDescendants().OfType<ComboBox>().Single().SelectedItem = "NetEase";
            Assert.True(PumpUntil(() => asks.Count == 3));
            Assert.Equal("NetEase", asks[2].Sources);
        }
        finally { win.Close(); PumpUntil(() => !win.IsVisible); }
    }

    [AvaloniaFact]
    public void Escape_WhileSearching_CancelsTheSearch_AndCloses()
    {
        EnsureAppStyles();
        var token = CancellationToken.None;
        var vm = Vm(async (_, _, _, ct) =>
        {
            token = ct;
            await Task.Delay(Timeout.Infinite, ct);
            return Hits();
        });
        var (_, win, host) = Open(vm);
        Assert.True(PumpUntil(() => CardSettledOpen(host) && token.CanBeCanceled));
        Assert.True(vm.IsSearching);
        AssertAnimatedCloseOnce(win, host, () => win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None));
        Assert.True(token.IsCancellationRequested);
    }

    // ── Real-Skia shots ──

    private static string ShotsDir => Path.Combine(@"D:\NoctisLyricsLab\search-popups",
        Environment.GetEnvironmentVariable("NOCTIS_SHOTS_PHASE") is { Length: > 0 } phase ? phase : "lyrics-after");

    /// <summary>Real Skia only (NOCTIS_TEST_SKIA=1): the dialog with results, while searching,
    /// and with nothing found, over a busy owner. NOCTIS_SHOTS_PHASE picks the folder
    /// (lyrics-before / lyrics-after) so the two sets line up by name.</summary>
    [AvaloniaFact]
    public void Probe_SavesTheSearchLyricsDialog()
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        EnsureAppStyles();
        Directory.CreateDirectory(ShotsDir);
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var owner = Owner();
            try
            {
                Shot(owner, "01-results.png", Vm(), run: true);
                var hang = Vm((_, _, _, ct) => Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => (IReadOnlyList<LyricsSourceHit>)Array.Empty<LyricsSourceHit>(), TaskScheduler.Default));
                Shot(owner, "02-searching.png", hang, run: true, settle: false);
                Shot(owner, "03-not-found.png", Vm((_, _, _, _) => Task.FromResult<IReadOnlyList<LyricsSourceHit>>(new[]
                {
                    new LyricsSourceHit("LRCLIB", null, false),
                    new LyricsSourceHit("Kugou", null, false),
                    new LyricsSourceHit("NetEase", null, false),
                })), run: true);
            }
            finally { owner.Close(); }
        });
    }

    private void Shot(Window owner, string name, LyricsSearchViewModel vm, bool run, bool settle = true)
    {
        var search = run ? vm.SearchAsync() : Task.CompletedTask;
        if (settle) PumpUntil(() => search.IsCompleted);
        var win = new LyricsSearchDialog(vm) { RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
        _ = win.ShowDialog(owner);
        PumpUntil(() => false, 700);
        var path = Path.Combine(ShotsDir, name);
        win.CaptureRenderedFrame()!.Save(path, PngBitmapEncoderOptions.Default);
        _o.WriteLine(path);
        win.Close();
        PumpUntil(() => !win.IsVisible, 2000);
    }

    /// <summary>A busy owner, so the backdrop (blurred or not) shows what sits behind.</summary>
    private static Window Owner()
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
        return owner;
    }
}
