using System;
using System.Collections.Generic;
using System.Diagnostics;
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
/// The Find Metadata tool (Settings → Find Metadata). Its behaviour — identify all, per-row
/// tick, apply only the ticked rows, roll a row back when its tag write fails — and, since
/// owner 10-08 (same UI + animation for search metadata), the pill dialog shell it shares with
/// the Metadata editor: every close path animates out once.
/// </summary>
public class MetadataFinderDialogTests
{
    private readonly ITestOutputHelper _o;
    public MetadataFinderDialogTests(ITestOutputHelper o) => _o = o;

    private static Track T(string title, string artist, string album) => new()
    {
        Id = Guid.NewGuid(), Title = title, Artist = artist, Album = album, AlbumArtist = "",
        FilePath = TestPaths.Primary("Music", Guid.NewGuid().ToString("N") + ".flac"),
    };

    /// <summary>Answers every track with one suggestion, synchronously (as a cache hit would).</summary>
    private sealed class SyncFinder : IMetadataFinderService
    {
        public Func<Track, TagSuggestion?> Answer { get; set; } =
            t => new TagSuggestion("Fixed " + t.Title, "Bad Bunny", "nadie sabe", 2023, 0.9, "Deezer");
        public int Calls;
        public Task<IReadOnlyList<TagSuggestion>> IdentifyAsync(Track track, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            ct.ThrowIfCancellationRequested();
            var s = Answer(track);
            return Task.FromResult<IReadOnlyList<TagSuggestion>>(s is null ? Array.Empty<TagSuggestion>() : new[] { s });
        }
    }

    /// <summary>Never answers until cancelled, so an identify run stays busy.</summary>
    private sealed class HangingFinder : IMetadataFinderService
    {
        public CancellationToken Seen;
        public async Task<IReadOnlyList<TagSuggestion>> IdentifyAsync(Track track, CancellationToken ct = default)
        {
            Seen = ct;
            await Task.Delay(Timeout.Infinite, ct);
            return Array.Empty<TagSuggestion>();
        }
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

    [AvaloniaFact]
    public async Task IdentifyAll_CountsEveryMatch_AndTicksConfidentOnes()
    {
        var finder = new SyncFinder();
        finder.Answer = t => t.Title == "b"
            ? new TagSuggestion("B", "X", "Y", null, 0.5, "MusicBrainz")   // below auto-tick
            : t.Title == "c" ? null
            : new TagSuggestion("Fixed " + t.Title, "Bad Bunny", "nadie sabe", 2023, 0.9, "Deezer");
        var vm = new MetadataFinderViewModel(new[] { T("a", "", ""), T("b", "", ""), T("c", "", ""), T("d", "", "") },
            finder, new SearchPopupsShotsTests.OkTags(), new FakeLibraryService());

        await vm.IdentifyAllCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(4, finder.Calls);
        Assert.Equal(new[] { true, false, false, true }, vm.Rows.Select(r => r.Apply));
        Assert.Equal(new[] { true, true, false, true }, vm.Rows.Select(r => r.HasProposal));
        Assert.Equal("No match", vm.Rows[2].Status);
        Assert.Equal("Review", vm.Rows[1].Status);
        // Every match counts, including the last row's (its result used to land after the summary).
        Assert.Equal("Identified 3 of 4", vm.StatusMessage);
        Assert.True(vm.HasSelection);
        Assert.False(vm.IsBusy);
    }

    [AvaloniaFact]
    public async Task ApplySelected_WritesOnlyTickedRows_AndRollsBackAFailedWrite()
    {
        var tags = new SearchPopupsShotsTests.OkTags();
        var library = new FakeLibraryService();
        var vm = new MetadataFinderViewModel(new[] { T("a", "Unknown Artist", "Unknown Album"), T("b", "", ""), T("c", "", "") },
            new SyncFinder(), tags, library);
        await vm.IdentifyAllCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        vm.Rows[2].Apply = false;             // per-row tick: c stays as it is
        tags.Write = t => t.Title != "Fixed b"; // b's file is locked
        await vm.ApplySelectedCommand.ExecuteAsync(null);

        Assert.Equal("Fixed a", vm.Rows[0].Track.Title);
        Assert.Equal("Bad Bunny", vm.Rows[0].Track.Artist);
        Assert.Equal("Bad Bunny", vm.Rows[0].Track.AlbumArtist);
        Assert.Equal(2023, vm.Rows[0].Track.Year);
        Assert.Equal("Applied", vm.Rows[0].Status);
        // The failed write left the library on the file's real tags.
        Assert.Equal("b", vm.Rows[1].Track.Title);
        Assert.Equal("", vm.Rows[1].Track.Artist);
        Assert.Equal("Write failed", vm.Rows[1].Status);
        Assert.Equal("c", vm.Rows[2].Track.Title);
        Assert.Equal("Applied 1, 1 failed", vm.StatusMessage);
    }

    // ── The pill dialog shell (owner 10-08: same UI + animation for search metadata) ──

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static (MetadataFinderViewModel vm, MetadataFinderDialog win, PillDialogHost host) Open(
        IMetadataFinderService? finder = null, int rows = 3, SearchPopupsShotsTests.OkTags? tags = null)
    {
        var tracks = Enumerable.Range(0, rows).Select(i => T($"Track {i:00}", "Unknown Artist", "Unknown Album")).ToArray();
        var vm = new MetadataFinderViewModel(tracks, finder ?? new SyncFinder(), tags ?? new SearchPopupsShotsTests.OkTags(), new FakeLibraryService());
        var win = new MetadataFinderDialog(vm) { RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
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

    /// <summary>The trigger starts the close: the card animates out while the window stays
    /// up, then the window closes exactly once.</summary>
    private void AssertAnimatedCloseOnce(MetadataFinderDialog win, PillDialogHost host, Action trigger)
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
            var (vm, win, host) = Open(rows: 400);
            try
            {
                Assert.True(PumpUntil(() => CardSettledOpen(host)), "open animation never settled");
                Assert.True(host.IsOpenStarted);
                Assert.Equal(new CornerRadius(30), host.CornerRadius);

                var all = win.GetVisualDescendants().ToList();
                // The editor's footer: quiet Cancel pill, solid accent Apply pill (owner 10-08).
                var apply = all.OfType<Button>().Single(b => b.Command == vm.ApplySelectedCommand);
                PillDialogHostTests.AssertSolidAccent(apply);
                var cancel = all.OfType<Button>().Single(b => b.Command == vm.CancelCommand);
                Assert.Contains("pill-secondary", cancel.Classes);
                Assert.Equal(Color.Parse("#1CFFFFFF"), AccentTestHarness.ColorOf(cancel.Background));
                // The header action is the editor's "Find online" pill; no round X any more.
                var identify = all.OfType<Button>().Single(b => b.Command == vm.IdentifyAllCommand);
                Assert.Contains("pill-secondary", identify.Classes);
                Assert.DoesNotContain(all.OfType<Button>(), b => b.Classes.Contains("dialog-close"));
                // The sources line moved into the ⓘ tooltip.
                var info = all.OfType<PathIcon>().Single(p => p.Name == "SourcesInfo");
                Assert.Equal(vm.SourceHint, ToolTip.GetTip(info));

                // Rows are filled pill wells, and still virtualized inside the card.
                var list = all.OfType<ItemsControl>().First(ic => ReferenceEquals(ic.ItemsSource, vm.Rows));
                var realized = list.GetRealizedContainers().Count();
                _o.WriteLine($"realized rows: {realized} of {vm.Rows.Count}; card {host.Card!.Bounds}");
                Assert.InRange(realized, 1, 59);
                var row = list.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("mf-row"));
                Assert.Equal(Color.Parse("#1CFFFFFF"), AccentTestHarness.ColorOf(row.Background));
                Assert.True(host.Card!.Bounds.Height <= 680.5, $"card {host.Card.Bounds.Height} tall");
            }
            finally { win.Close(); PumpUntil(() => !win.IsVisible); }
        });
    }

    [AvaloniaFact]
    public void Dialog_WithNothingToFix_ShowsTheStatusInPlaceOfTheList()
    {
        EnsureAppStyles();
        var (vm, win, host) = Open(rows: 0);
        try
        {
            Assert.True(PumpUntil(() => CardSettledOpen(host)));
            Assert.False(vm.HasRows);
            Assert.Contains(win.GetVisualDescendants().OfType<TextBlock>(),
                t => t.Text == vm.StatusMessage && t.IsEffectivelyVisible && t.FontSize > 12.9);
            Assert.DoesNotContain(win.GetVisualDescendants().OfType<ScrollViewer>(), s => s.IsEffectivelyVisible);
            // The card hugs the short content instead of a tall empty box.
            Assert.True(host.Card!.Bounds.Height < 400, $"card {host.Card.Bounds.Height} tall");
        }
        finally { win.Close(); PumpUntil(() => !win.IsVisible); }
    }

    [AvaloniaFact]
    public void CancelButton_AnimatesThenClosesOnce()
    {
        EnsureAppStyles();
        var (vm, win, host) = Open();
        var cancel = win.GetVisualDescendants().OfType<Button>().Single(b => b.Command == vm.CancelCommand);
        AssertAnimatedCloseOnce(win, host, () => Click(win, cancel));
    }

    [AvaloniaFact]
    public void Escape_AnimatesThenClosesOnce()
    {
        EnsureAppStyles();
        var (_, win, host) = Open();
        AssertAnimatedCloseOnce(win, host, () => win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None));
    }

    [AvaloniaFact]
    public void ViewModelClosed_AnimatesThenClosesOnce()
    {
        EnsureAppStyles();
        var (vm, win, host) = Open();
        AssertAnimatedCloseOnce(win, host, () => vm.CancelCommand.Execute(null));
    }

    [AvaloniaFact]
    public void DirectClose_AnimatesThenClosesOnce()
    {
        EnsureAppStyles();
        var (_, win, host) = Open();
        // Window.Close() is what Alt+F4 comes down to.
        AssertAnimatedCloseOnce(win, host, () => win.Close());
    }

    [AvaloniaFact]
    public void ApplyButton_WritesTheTickedRows_AndTheDialogStaysOpen()
    {
        EnsureAppStyles();
        var tags = new SearchPopupsShotsTests.OkTags();
        var written = new List<string>();
        tags.Write = t => { lock (written) written.Add(t.Title); return true; };
        var (vm, win, host) = Open(tags: tags);
        try
        {
            Assert.True(PumpUntil(() => CardSettledOpen(host)));
            var identify = win.GetVisualDescendants().OfType<Button>().Single(b => b.Command == vm.IdentifyAllCommand);
            Click(win, identify);
            Assert.True(PumpUntil(() => vm.HasSelection && !vm.IsBusy));
            vm.Rows[1].Apply = false;
            PumpUntil(() => false, 50);

            var apply = win.GetVisualDescendants().OfType<Button>().Single(b => b.Command == vm.ApplySelectedCommand);
            Assert.True(apply.IsEffectivelyEnabled);
            Click(win, apply);
            Assert.True(PumpUntil(() => vm.Rows[0].Status == "Applied"), $"status {vm.Rows[0].Status}");
            Assert.Equal(new[] { "Fixed Track 00", "Fixed Track 02" }, written);
            Assert.Equal("Track 01", vm.Rows[1].Track.Title);
            Assert.False(host.IsClosing);
            Assert.True(win.IsVisible);
        }
        finally { win.Close(); PumpUntil(() => !win.IsVisible); }
    }

    [AvaloniaFact]
    public void Escape_WhileIdentifying_StopsTheRun_ThenASecondEscCloses()
    {
        EnsureAppStyles();
        var finder = new HangingFinder();
        var (vm, win, host) = Open(finder);
        var closed = 0;
        win.Closed += (_, _) => closed++;
        try
        {
            Assert.True(PumpUntil(() => CardSettledOpen(host)));
            var run = vm.IdentifyAllCommand.ExecuteAsync(null);
            Assert.True(PumpUntil(() => vm.IsBusy && finder.Seen.CanBeCanceled));

            win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.True(finder.Seen.IsCancellationRequested);
            Assert.False(host.IsClosing, "Esc closed the dialog instead of stopping the run");
            Assert.True(PumpUntil(() => run.IsCompleted && !vm.IsBusy));
            Assert.Equal("Cancelled", vm.StatusMessage);

            win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.True(host.IsClosing);
            Assert.True(PumpUntil(() => closed > 0, 2000));
            Assert.Equal(1, closed);
        }
        finally { if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); } }
    }

    [AvaloniaFact]
    public void ClosingTheWindow_StopsARunningIdentify()
    {
        EnsureAppStyles();
        var finder = new HangingFinder();
        var (vm, win, host) = Open(finder);
        Assert.True(PumpUntil(() => CardSettledOpen(host)));
        var run = vm.IdentifyAllCommand.ExecuteAsync(null);
        Assert.True(PumpUntil(() => vm.IsBusy && finder.Seen.CanBeCanceled));

        win.Close();
        Assert.True(PumpUntil(() => !win.IsVisible, 2000));
        Assert.True(finder.Seen.IsCancellationRequested, "the identify kept running after the window closed");
        Assert.True(PumpUntil(() => run.IsCompleted));
    }
}
