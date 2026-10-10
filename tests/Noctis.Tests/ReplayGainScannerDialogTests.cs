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
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-08: Scan ReplayGain as the rounded pill pop-up (blurred app behind, quiet
/// Cancel, solid accent Scan), the option in plain words, rows as filled cards with a state
/// chip and the measured gain, plus the bugs found on the way. No real ffmpeg.
/// </summary>
public class ReplayGainScannerDialogTests
{
    private readonly ITestOutputHelper _o;
    public ReplayGainScannerDialogTests(ITestOutputHelper o) => _o = o;

    /// <summary>Reports like ReplayGainScannerService: "Measuring…" per track in a first pass,
    /// then "Done" with the gains in a second. With <see cref="BlockAfterMeasuring"/> it waits
    /// for the token after that many measurements and lets the cancel propagate (as the
    /// service's MeasureAsync rethrows it, without a last report).</summary>
    private sealed class FakeScanner : IReplayGainScannerService
    {
        public bool Available = true;
        public int? BlockAfterMeasuring;
        public Exception? Throw;
        public int Runs;
        public bool LastAlbumMode;
        public IReadOnlyList<Track> LastTracks = Array.Empty<Track>();
        public readonly TaskCompletionSource Blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsAvailable => Available;

        public async Task<ScanSummary> ScanAsync(IReadOnlyList<Track> tracks, bool albumMode, IProgress<ScanProgress> progress, CancellationToken ct)
        {
            Runs++;
            LastTracks = tracks;
            LastAlbumMode = albumMode;
            if (Throw != null) throw Throw;
            var i = 0;
            foreach (var t in tracks)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report(new ScanProgress { Track = t, Status = "Measuring…" });
                if (++i == BlockAfterMeasuring)
                {
                    Blocked.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                }
            }
            foreach (var t in tracks)
                progress.Report(new ScanProgress
                {
                    Track = t, Status = "Done", Done = true, TrackGainDb = -6.2, AlbumGainDb = albumMode ? -7.1 : 0.0,
                });
            return new ScanSummary { Scanned = tracks.Count };
        }
    }

    private static List<Track> Tracks(int n = 2) => Enumerable.Range(1, n)
        .Select(i => new Track
        {
            Id = Guid.NewGuid(), Title = $"Song {i}", Artist = "Bad Bunny", AlbumArtist = "Bad Bunny",
            Album = "Album", TrackNumber = i, FilePath = TestPaths.Primary("Music", $"rg{i}.flac"),
        })
        .ToList();

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

    // ── The dialog ──

    [AvaloniaFact]
    public void Scanner_OpensInPillHost_WithResolvedStyles_AndBoundActions()
    {
        EnsureAppStyles();
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var vm = new ReplayGainScannerViewModel(Tracks(1), new FakeScanner(), new FakeLibraryService(),
                path => new Track { FilePath = path, Title = path });
            var win = new ReplayGainScannerDialog(vm) { RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
            win.Show();
            var closed = 0;
            win.Closed += (_, _) => closed++;
            try
            {
                var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
                Assert.True(PumpUntil(() => host.Card is { Opacity: > 0.999 } && host.BackdropLayer!.Opacity > 0.999),
                    "open animation never settled");
                Assert.Equal(new CornerRadius(30), host.CornerRadius);

                var all = win.GetVisualDescendants().ToList();
                Assert.DoesNotContain(all.OfType<Button>(), b => b.Classes.Contains("accent-btn"));
                // Plain words, not "group by AlbumId".
                Assert.DoesNotContain(all.OfType<CheckBox>(), c => Equals(c.Content, Loc.T("ReplayGainScanner.ComputeAlbumGainGroup")));
                Assert.Contains(all.OfType<CheckBox>(), c => Equals(c.Content, Loc.T("ReplayGainScanner.AlbumGain")));

                var scan = all.OfType<Button>().Single(b => b.Command == vm.StartCommand);
                PillDialogHostTests.AssertSolidAccent(scan);
                Assert.True(scan.IsEffectivelyEnabled);
                var cancel = all.OfType<Button>().Single(b => b.Command == vm.CancelCommand);
                Assert.Contains("pill-secondary", cancel.Classes);
                Assert.Equal(Color.Parse("#1CFFFFFF"), AccentTestHarness.ColorOf(cancel.Background));
                var add = win.FindControl<Button>("AddFilesButton")!;
                Assert.Contains("pill-secondary", add.Classes);
                Assert.True(add.IsEffectivelyVisible && add.IsEffectivelyEnabled);

                // Rows: a filled card with a tick box and a state chip.
                var list = all.OfType<ItemsControl>().Single(ic => ReferenceEquals(ic.ItemsSource, vm.Jobs));
                Assert.Single(list.GetRealizedContainers());
                var row = list.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("pill-well"));
                var tick = row.GetVisualDescendants().OfType<CheckBox>().Single();
                Assert.True(tick.IsEffectivelyEnabled);
                Assert.Contains(row.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Loc.T("ReplayGainScanner.StatePending"));

                // Unticking the only row turns Scan off (it used to stay on and do nothing).
                vm.Jobs[0].IsIncluded = false;
                PumpUntil(() => false, 30);
                Assert.False(scan.IsEffectivelyEnabled);
                vm.Jobs[0].IsIncluded = true;
                PumpUntil(() => false, 30);
                Assert.True(scan.IsEffectivelyEnabled);

                // Esc closes like Cancel: animated, exactly once.
                win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                Assert.True(host.IsClosing);
                Assert.True(PumpUntil(() => closed > 0, 2000), "window never closed");
                PumpUntil(() => false, 250);
                Assert.Equal(1, closed);
            }
            finally { if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); } }
        });
    }

    /// <summary>Esc during a scan stops it and keeps the dialog; the row being measured reads
    /// Cancelled (it used to keep "Measuring…" and its spinner). Row boxes and Add files are
    /// locked while it runs.</summary>
    [AvaloniaFact]
    public void Escape_DuringScan_StopsIt_AndUnsticksTheRow()
    {
        EnsureAppStyles();
        var fake = new FakeScanner { BlockAfterMeasuring = 1 };
        var vm = new ReplayGainScannerViewModel(Tracks(2), fake, new FakeLibraryService(),
            path => new Track { FilePath = path, Title = path });
        var win = new ReplayGainScannerDialog(vm) { Width = 1100, Height = 820 };
        win.Show();
        var closed = 0;
        win.Closed += (_, _) => closed++;
        try
        {
            var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
            Assert.True(PumpUntil(() => host.Card is { Opacity: > 0.999 }));
            vm.StartCommand.Execute(null);
            Assert.True(PumpUntil(() => fake.Blocked.Task.IsCompleted && vm.Jobs[0].IsWorking), "scan never started");
            Assert.False(vm.CanEditSelection);
            Assert.False(win.FindControl<Button>("AddFilesButton")!.IsEffectivelyEnabled);
            var ticks = win.GetVisualDescendants().OfType<CheckBox>().Where(c => c.DataContext is ReplayGainScannerViewModel.RgJobRow).ToList();
            Assert.NotEmpty(ticks);
            Assert.All(ticks, c => Assert.False(c.IsEffectivelyEnabled));
            Assert.Equal(Loc.T("ReplayGainScanner.Stop"), vm.CancelLabel);

            win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.True(PumpUntil(() => !vm.IsScanning), "scan never stopped");
            Assert.Equal(0, closed);
            Assert.False(host.IsClosing);
            Assert.Equal(Loc.T("ReplayGainScanner.Cancelled"), vm.StatusMessage);
            Assert.All(vm.Jobs, j => Assert.Equal(ReplayGainScannerViewModel.RgState.Cancelled, j.State));
            Assert.DoesNotContain(vm.Jobs, j => j.IsWorking);
            Assert.True(vm.CanEditSelection);

            win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.True(PumpUntil(() => closed > 0, 2000));
            PumpUntil(() => false, 250);
            Assert.Equal(1, closed);
        }
        finally { if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); } }
    }

    // ── View model bugs ──

    /// <summary>Bug: album mode measures every track before writing any, so each row read
    /// "Measuring…" with a spinner from its turn until the whole selection was measured.</summary>
    [Fact]
    public async Task MeasuredRows_StopReadingMeasuring_WhenTheNextOneStarts()
    {
        var fake = new FakeScanner { BlockAfterMeasuring = 2 };
        var vm = new ReplayGainScannerViewModel(Tracks(3), fake, new FakeLibraryService());
        var run = vm.StartCommand.ExecuteAsync(null);
        await fake.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ReplayGainScannerViewModel.RgState.Measured, vm.Jobs[0].State);
        Assert.Equal(ReplayGainScannerViewModel.RgState.Working, vm.Jobs[1].State);
        Assert.Equal(ReplayGainScannerViewModel.RgState.Pending, vm.Jobs[2].State);

        vm.CancelCommand.Execute(null);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(vm.Jobs, j => j.IsWorking);
    }

    /// <summary>Bug: with album gain off the row read "A: +0.00 dB" — an album gain that was
    /// never computed or written.</summary>
    [Fact]
    public async Task AlbumGainOff_ShowsNoAlbumGain()
    {
        var fake = new FakeScanner();
        var vm = new ReplayGainScannerViewModel(Tracks(1), fake, new FakeLibraryService()) { AlbumMode = false };
        await vm.StartCommand.ExecuteAsync(null);

        Assert.False(fake.LastAlbumMode);
        var row = vm.Jobs[0];
        Assert.True(row.ShowGain);
        Assert.Equal((-6.2).ToString("+0.00;-0.00;0.00") + " dB", row.TrackGainText);
        Assert.DoesNotContain("A:", row.GainsText);

        var album = new ReplayGainScannerViewModel(Tracks(1), new FakeScanner(), new FakeLibraryService());
        await album.StartCommand.ExecuteAsync(null);
        Assert.Contains("A: " + (-7.1).ToString("+0.00;-0.00;0.00") + " dB", album.Jobs[0].GainsText);
    }

    /// <summary>Bug: a row unticked for a scan that was then cancelled kept "Skipped" after
    /// it was ticked back in.</summary>
    [Fact]
    public async Task RowTickedBackIn_AfterACancelledScan_IsNoLongerSkipped()
    {
        var fake = new FakeScanner { BlockAfterMeasuring = 1 };
        var vm = new ReplayGainScannerViewModel(Tracks(2), fake, new FakeLibraryService());
        vm.Jobs[1].IsIncluded = false;
        var run = vm.StartCommand.ExecuteAsync(null);
        await fake.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ReplayGainScannerViewModel.RgState.Skipped, vm.Jobs[1].State);
        vm.CancelCommand.Execute(null);
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        vm.Jobs[1].IsIncluded = true;
        Assert.Equal(ReplayGainScannerViewModel.RgState.Pending, vm.Jobs[1].State);
        Assert.Equal("Scan ReplayGain · 2 tracks", vm.TitleText);
    }

    /// <summary>Bug: after a finished scan "Add files…" stayed clickable but AddFilesAsync
    /// ignores it — a button that did nothing.</summary>
    [Fact]
    public async Task AddFiles_IsOff_OnceTheScanFinished()
    {
        var vm = new ReplayGainScannerViewModel(Tracks(1), new FakeScanner(), new FakeLibraryService(),
            path => new Track { FilePath = path, Title = path });
        Assert.True(vm.CanAddMore);
        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.HasFinished);
        Assert.False(vm.CanAddMore);
        Assert.False(vm.CanEditSelection);
        Assert.True(vm.StartCommand.CanExecute(null)); // "Done" still closes
    }

    /// <summary>Bug: an exception out of the scan left "Scanning…" in the footer.</summary>
    [Fact]
    public async Task ScanThrows_ShowsTheError_AndUnlocks()
    {
        var vm = new ReplayGainScannerViewModel(Tracks(2), new FakeScanner { Throw = new InvalidOperationException("boom") },
            new FakeLibraryService());
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(Loc.T("ReplayGainScanner.FailedWith", "boom"), vm.StatusMessage);
        Assert.False(vm.IsScanning);
        Assert.False(vm.HasFinished);
        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void NoFfmpeg_DisablesScan_AndSaysWhy()
    {
        var vm = new ReplayGainScannerViewModel(Tracks(1), new FakeScanner { Available = false }, new FakeLibraryService());
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.Equal(Loc.T("ReplayGainScanner.NoFfmpeg"), vm.StatusMessage);
    }

    /// <summary>Real Skia only (NOCTIS_TEST_SKIA=1): a PNG of the dialog over a busy owner,
    /// for eyeballing the layout. Saved under the system temp folder.</summary>
    [AvaloniaFact]
    public void Probe_SavesTheScannerDialog()
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        EnsureAppStyles();
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var stripes = new StackPanel();
            var colors = new[] { "#E74856", "#2D7DD2", "#F4D35E", "#3BB273", "#7B2CBF", "#FF8C42" };
            for (var i = 0; i < 18; i++)
                stripes.Children.Add(new Border { Height = 50, Background = new SolidColorBrush(Color.Parse(colors[i % colors.Length])) });
            var owner = new Window { Width = 1100, Height = 820, Content = stripes, RequestedThemeVariant = ThemeVariant.Dark };
            owner.Show();
            PumpUntil(() => false, 100);

            var vm = new ReplayGainScannerViewModel(Tracks(4), new FakeScanner(), new FakeLibraryService(),
                path => new Track { FilePath = path, Title = path });
            var win = new ReplayGainScannerDialog(vm) { RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
            _ = win.ShowDialog(owner);
            try
            {
                var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
                Assert.True(PumpUntil(() => host.Card is { Opacity: > 0.999 } && host.BackdropBitmap != null, 3000));
                vm.Jobs[0].Apply(new ScanProgress { Track = vm.Jobs[0].Track, Status = "Done", Done = true, TrackGainDb = -6.2, AlbumGainDb = -7.1 }, true);
                vm.Jobs[1].Apply(new ScanProgress { Track = vm.Jobs[1].Track, Status = "Measuring…" }, true);
                vm.Jobs[3].IsIncluded = false;
                PumpUntil(() => false, 150);
                var path = Path.Combine(Path.GetTempPath(), "noctis-replaygain-dialog.png");
                win.CaptureRenderedFrame()!.Save(path, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                _o.WriteLine(path);
            }
            finally
            {
                if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); }
                owner.Close();
            }
        });
    }
}
