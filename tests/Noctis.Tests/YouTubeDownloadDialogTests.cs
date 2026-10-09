using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
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
using Noctis.Services.YouTube;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-08: "Add from YouTube" moves into the rounded pill pop-up (PillDialogHost, blurred
/// backdrop, pill fields/buttons). The dialog mounts in the host with the pill styles resolved,
/// its actions are bound (search, close, per-card Download, Esc), and with yt-dlp missing the
/// body is one clear Install action. No network: the tool and the import service are fakes.
/// </summary>
public class YouTubeDownloadDialogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noctis-ytdialog-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ITestOutputHelper _o;
    public YouTubeDownloadDialogTests(ITestOutputHelper o) => _o = o;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

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

    private static bool CardSettledOpen(PillDialogHost host) =>
        host.Card is { } card && card.Opacity > 0.999 && host.BackdropLayer!.Opacity > 0.999;

    private sealed class FakeService : IYouTubeImportService
    {
        public FakeService(YtDlpTool tool) => Tool = tool;
        public YtDlpTool Tool { get; }
        public List<YouTubeTrackInfo> Found { get; } = new();
        public int Imports;
        public string ResolveDownloadFolder() => "C:/Music/YouTube";
        public Task<List<YouTubeTrackInfo>> SearchAsync(string query, CancellationToken ct) => Task.FromResult(Found.ToList());
        public Task<YouTubeTrackInfo?> ResolveAsync(string urlOrId, CancellationToken ct) => Task.FromResult(Found.FirstOrDefault());
        public Task<YouTubeImportResult> ImportAsync(YouTubeTrackInfo info, IProgress<YouTubeImportProgress>? progress, CancellationToken ct)
        {
            Imports++;
            return Task.FromResult(new YouTubeImportResult("C:/Music/YouTube/x.m4a", info.Channel, info.Title, AddedToLibrary: true));
        }
    }

    /// <summary>A tool with every process / network seam faked; an app-installed copy when asked.</summary>
    private YtDlpTool MakeTool(bool installed)
    {
        var tool = new YtDlpTool(new HttpClient(), _root, () => string.Empty);
        if (installed)
        {
            Directory.CreateDirectory(tool.ToolsDirectory);
            File.WriteAllText(tool.InstalledPath, "fake");
        }
        tool.JsRuntimes = (true, false);
        tool.LatestVersionFetcher = _ => Task.FromResult<string?>("2026.09.01");
        tool.Updater = _ => Task.CompletedTask;
        tool.Runner = (_, args, _, _) => Task.FromResult(args.Count == 1 && args[0] == "--version"
            ? (0, "2026.09.01\n", string.Empty)
            : (1, string.Empty, "not in tests"));
        return tool;
    }

    private static YouTubeTrackInfo Info(string id, string title, string channel) =>
        new(id, "https://www.youtube.com/watch?v=" + id, title, channel, TimeSpan.FromSeconds(214), null, null, null, null, null, null);

    private static (YouTubeDownloadDialog win, PillDialogHost host) Show(YouTubeDownloadViewModel vm)
    {
        var win = new YouTubeDownloadDialog(vm) { RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
        win.Show();
        var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
        return (win, host);
    }

    /// <summary>
    /// Live 10-08: yt-dlp came from PATH (Chocolatey, 2026.07.04), which Noctis never replaces,
    /// and with the Settings "Install / update" row gone nothing could update it. The footer
    /// now offers Update (Noctis's own copy, which then wins over PATH) — only for a copy it
    /// may not replace and only while a newer release is known.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(true)]   // an outdated copy Noctis may not replace (PATH / custom path)
    [InlineData(false)]  // Noctis's own copy, current
    public void OutdatedForeignCopy_FooterOffersUpdate_OwnCurrentCopyDoesNot(bool foreignOutdated)
    {
        EnsureAppStyles();
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            YtDlpTool tool;
            if (foreignOutdated)
            {
                var custom = Path.Combine(_root, "custom", "yt-dlp.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(custom)!);
                File.WriteAllText(custom, "fake");
                tool = new YtDlpTool(new HttpClient(), _root, () => custom)
                {
                    JsRuntimes = (true, false),
                    LatestVersionFetcher = _ => Task.FromResult<string?>("2026.09.01"),
                    Updater = _ => Task.CompletedTask,
                    Runner = (_, args, _, _) => Task.FromResult(args.Count == 1 && args[0] == "--version"
                        ? (0, "2026.07.04\n", string.Empty) : (1, string.Empty, "not in tests")),
                };
            }
            else tool = MakeTool(installed: true);

            var vm = new YouTubeDownloadViewModel(new FakeService(tool), new HttpClient());
            var (win, host) = Show(vm);
            try
            {
                Assert.True(PumpUntil(() => CardSettledOpen(host)), "open animation never settled");
                Assert.True(PumpUntil(() => vm.HasToolVersion), "version note never showed");
                var update = win.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "UpdateToolButton");
                if (foreignOutdated)
                {
                    Assert.True(PumpUntil(() => vm.ShowToolUpdate && update.IsEffectivelyVisible),
                        $"no Update pill (note '{vm.ToolVersionText}')");
                    Assert.Same(vm.InstallToolCommand, update.Command);
                    Assert.Contains("pill-secondary", update.Classes);
                }
                else
                {
                    PumpUntil(() => false, 200);
                    Assert.False(vm.ShowToolUpdate);
                    Assert.False(update.IsEffectivelyVisible);
                }
            }
            finally { win.Close(); PumpUntil(() => !win.IsVisible); }
        });
    }

    [AvaloniaFact]
    public void Dialog_OpensInPillHost_WithResolvedStyles_AndActionsBound()
    {
        EnsureAppStyles();
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var service = new FakeService(MakeTool(installed: true));
            service.Found.Add(Info("a1", "Artist One - First Song", "Artist One"));
            service.Found.Add(Info("b2", "Artist Two - Second Song", "Artist Two"));
            var vm = new YouTubeDownloadViewModel(service, new HttpClient(), initialQuery: "song");
            var (win, host) = Show(vm);
            try
            {
                Assert.True(PumpUntil(() => CardSettledOpen(host)), "open animation never settled");
                Assert.Equal(new CornerRadius(30), host.CornerRadius);
                Assert.True(vm.ToolInstalled);

                var all = win.GetVisualDescendants().ToList();
                // No leftovers of the old outlined look or its hand-made overlay.
                Assert.DoesNotContain(all.OfType<TextBox>(), b => b.Classes.Contains("pill"));
                Assert.Null(win.FindControl<Border>("DialogOverlay"));

                // Search: filled pill field (tone + radius from PillDialog.axaml) and the round
                // solid-accent search button.
                var query = all.OfType<TextBox>().Single(b => b.Name == "QueryBox");
                Assert.Contains("pill-field", query.Classes);
                Assert.True(query.IsEffectivelyEnabled);
                var chrome = query.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_BorderElement");
                Assert.Equal(new CornerRadius(999), chrome.CornerRadius);
                // Focused on open: the focused fill tone and the accent ring.
                Assert.True(PumpUntil(() => query.IsFocused), "search box not focused on open");
                Assert.True(PumpUntil(() => AccentTestHarness.ColorOf(chrome.Background) == Color.Parse("#14FFFFFF")),
                    $"field fill {AccentTestHarness.ColorOf(chrome.Background)}");
                Assert.Equal(Color.Parse("#E74856"), AccentTestHarness.ColorOf(chrome.BorderBrush));
                var search = all.OfType<Button>().Single(b => b.Command == vm.SearchCommand);
                PillDialogHostTests.AssertSolidAccent(search);

                // Footer: Close is the quiet pill.
                var close = all.OfType<Button>().Single(b => b.Command == vm.CloseCommand);
                Assert.Contains("pill-secondary", close.Classes);
                Assert.Equal(Color.Parse("#1CFFFFFF"), AccentTestHarness.ColorOf(close.Background));

                // No setup chrome once yt-dlp is there; the results are pill-well cards with a
                // bound Download pill each.
                Assert.False(win.FindControl<StackPanel>("SetupPanel")!.IsVisible);
                Assert.Equal(2, vm.Results.Count);
                Assert.True(PumpUntil(() => win.GetVisualDescendants().OfType<Border>()
                    .Count(b => b.Classes.Contains("yt-row") && b.IsEffectivelyVisible) == 2), "result cards not shown");
                var cards = win.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("yt-row")).ToList();
                Assert.All(cards, c => Assert.Contains("pill-well", c.Classes));
                var downloads = win.GetVisualDescendants().OfType<Button>()
                    .Where(b => vm.Results.Any(r => ReferenceEquals(b.Command, r.DownloadCommand))).ToList();
                Assert.Equal(2, downloads.Count);
                Assert.All(downloads, b => Assert.Contains("pill-secondary", b.Classes));

                // Download on the first card: imported, the card takes the accent ring.
                downloads[0].Command!.Execute(null);
                var row = vm.Results[0];
                Assert.True(PumpUntil(() => row.IsDone), "download never finished");
                Assert.Equal(1, service.Imports);
                Assert.Equal("In your library", row.StatusText);
                Assert.True(PumpUntil(() => cards[0].Classes.Contains("done")));
                Assert.False(downloads[0].IsVisible);

                // Esc closes through the host's animated close.
                var closed = 0;
                win.Closed += (_, _) => closed++;
                win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                Assert.True(host.IsClosing);
                Assert.True(PumpUntil(() => closed > 0, 2000));
                Assert.Equal(1, closed);
            }
            finally { if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); } }
        });
    }

    /// <summary>Real Skia only: PNGs of the results and setup states, for eyeballing the layout.</summary>
    [AvaloniaFact]
    public void Probe_SavesTheDialogStates()
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        EnsureAppStyles();
        var dir = Path.Combine(Path.GetTempPath(), "noctis-youtube-dialog-shots");
        Directory.CreateDirectory(dir);
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var service = new FakeService(MakeTool(installed: true));
            service.Found.Add(Info("a1", "Artist One - First Song (Official Video)", "Artist One"));
            service.Found.Add(Info("b2", "Artist Two - Second Song", "Artist Two"));
            service.Found.Add(Info("c3", "Artist Three - Third Song (Live)", "Artist Three"));
            var vm = new YouTubeDownloadViewModel(service, new HttpClient(), initialQuery: "song");
            var (win, host) = Show(vm);
            try
            {
                Assert.True(PumpUntil(() => CardSettledOpen(host)));
                vm.Results[0].DownloadCommand.Execute(null);
                vm.Results[1].IsBusy = true;
                vm.Results[1].Progress = 0.42;
                vm.Results[1].StatusText = "Downloading audio…";
                vm.Results[2].Failed = true;
                vm.Results[2].StatusText = "ERROR: Video unavailable";
                vm.ToolVersionText = "yt-dlp 2026.09.01";
                PumpUntil(() => false, 300);
                win.CaptureRenderedFrame()!.Save(Path.Combine(dir, "01-results.png"));
                vm.ToolInstalled = false;
                vm.IsInstallingTool = true;
                vm.InstallProgress = 0.6;
                vm.StatusMessage = "Downloading yt-dlp…";
                PumpUntil(() => false, 300);
                win.CaptureRenderedFrame()!.Save(Path.Combine(dir, "02-setup.png"));
                _o.WriteLine(dir);
            }
            finally { if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); } }
        });
    }

    [AvaloniaFact]
    public void ToolMissing_ShowsOnlyTheInstallAction()
    {
        EnsureAppStyles();
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var vm = new YouTubeDownloadViewModel(new FakeService(MakeTool(installed: false)), new HttpClient());
            // yt-dlp on this machine's PATH would count as installed: force the missing state.
            vm.ToolInstalled = false;
            var (win, host) = Show(vm);
            try
            {
                Assert.True(PumpUntil(() => CardSettledOpen(host)), "open animation never settled");
                var setup = win.FindControl<StackPanel>("SetupPanel")!;
                Assert.True(setup.IsEffectivelyVisible);

                // The setup panel's Install (the footer's Update pill shares the command).
                var install = win.GetVisualDescendants().OfType<Button>()
                    .Single(b => b.Command == vm.InstallToolCommand && b.Name != "UpdateToolButton");
                Assert.True(install.IsEffectivelyVisible);
                Assert.True(install.IsEffectivelyEnabled);
                PillDialogHostTests.AssertSolidAccent(install);

                // Nothing else competes with it: search is off, no results / empty hint / version.
                Assert.False(win.FindControl<TextBox>("QueryBox")!.IsEffectivelyEnabled);
                Assert.False(vm.ShowResults);
                Assert.False(vm.ShowEmpty);
                Assert.False(vm.ShowSkeleton);
                Assert.False(vm.HasToolVersion);

                // Installed: the setup gives way to the empty hint and the search comes alive.
                vm.ToolInstalled = true;
                Assert.True(PumpUntil(() => !setup.IsVisible && vm.ShowEmpty));
                Assert.True(win.FindControl<TextBox>("QueryBox")!.IsEffectivelyEnabled);
            }
            finally { if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); } }
        });
    }
}
