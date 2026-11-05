using System.Diagnostics;
using System.IO.Compression;
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
using Noctis.Plugins;
using Noctis.Plugins.YouTubeImport;
using Noctis.Services.Plugins;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// "Add from YouTube" as the official plugin (10-29; it was the app's pill dialog, owner 10-08):
/// the window built in code mounts with Noctis's pill styles, its actions are bound (search,
/// format, folder, per-card Download, recent downloads, Esc), yt-dlp missing leaves one Install
/// action, downloads land in the library through API 1.5, and the Release zip installs and runs.
/// No network: yt-dlp, the importer and the library are fakes.
/// </summary>
public class YouTubeImportPluginTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noctis-ytplugin-view-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ITestOutputHelper _o;
    public YouTubeImportPluginTests(ITestOutputHelper o) => _o = o;

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

    private sealed class FakeImporter : IYouTubeImporter
    {
        public FakeImporter(YtDlpTool tool, bool ffmpeg = true) { Tool = tool; HasFfmpeg = ffmpeg; }
        public YtDlpTool Tool { get; }
        public bool HasFfmpeg { get; }
        public string Folder = "C:/Music/YouTube";
        public List<YouTubeTrackInfo> Found { get; } = new();
        public List<AudioFormat> Imports { get; } = new();
        public bool InLibrary = true;
        public string ResolveDownloadFolder() => Folder;
        public Task<List<YouTubeTrackInfo>> SearchAsync(string query, CancellationToken ct) => Task.FromResult(Found.ToList());
        public Task<YouTubeTrackInfo?> ResolveAsync(string urlOrId, CancellationToken ct) => Task.FromResult(Found.FirstOrDefault());
        public Task<YouTubeImportResult> ImportAsync(YouTubeTrackInfo info, AudioFormat format, IProgress<YouTubeImportProgress>? progress, CancellationToken ct)
        {
            Imports.Add(format);
            return Task.FromResult(new YouTubeImportResult($"C:/Music/YouTube/{info.Id}.m4a", info.Channel, info.Title, InLibrary));
        }
    }

    /// <summary>A tool with every process / network seam faked; an app-installed copy when asked.</summary>
    private YtDlpTool MakeTool(bool installed, string? custom = null, string version = "2026.09.01")
    {
        var tool = new YtDlpTool(new HttpClient(), Path.Combine(_root, "tools"), () => custom ?? string.Empty);
        if (installed)
        {
            Directory.CreateDirectory(tool.ToolsDirectory);
            File.WriteAllText(tool.InstalledPath, "fake");
        }
        tool.JsRuntimes = (true, false);
        tool.LatestVersionFetcher = _ => Task.FromResult<string?>("2026.09.01");
        tool.Updater = _ => Task.CompletedTask;
        tool.Runner = (_, args, _, _) => Task.FromResult(args.Count == 1 && args[0] == "--version"
            ? (0, version + "\n", string.Empty)
            : (1, string.Empty, "not in tests"));
        return tool;
    }

    /// <summary>The window's view with yt-dlp installed, under <paramref name="root"/> (PluginViewThemeTests).</summary>
    internal static YouTubeImportView NewView(string root)
    {
        var tool = new YtDlpTool(new HttpClient(), Path.Combine(root, "tools"), () => string.Empty);
        Directory.CreateDirectory(tool.ToolsDirectory);
        File.WriteAllText(tool.InstalledPath, "fake");
        tool.JsRuntimes = (true, false);
        tool.LatestVersionFetcher = _ => Task.FromResult<string?>("2026.09.01");
        tool.Updater = _ => Task.CompletedTask;
        tool.Runner = (_, args, _, _) => Task.FromResult(args.Count == 1 && args[0] == "--version"
            ? (0, "2026.09.01\n", string.Empty)
            : (1, string.Empty, "not in tests"));
        var vm = new YouTubeImportViewModel(new FakeImporter(tool), new HttpClient(), new ImportState(Path.Combine(root, "data")));
        return new YouTubeImportView(vm);
    }

    private static YouTubeTrackInfo Info(string id, string title, string channel) =>
        new(id, "https://www.youtube.com/watch?v=" + id, title, channel, TimeSpan.FromSeconds(214), null, null, null, null, null, null);

    private ImportState State() => new(Path.Combine(_root, "data"));

    private static (Window win, YouTubeImportView view) Show(YouTubeImportViewModel vm)
    {
        var view = new YouTubeImportView(vm);
        var win = new Window { RequestedThemeVariant = ThemeVariant.Dark, Width = 1000, Height = 680, Content = view };
        win.Show();
        return (win, view);
    }

    private static void Close(Window win)
    {
        if (!win.IsVisible) return;
        win.Close();
        PumpUntil(() => !win.IsVisible);
    }

    [AvaloniaFact]
    public void Window_UsesThePillStyles_AndItsActionsWork()
    {
        EnsureAppStyles();
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var importer = new FakeImporter(MakeTool(installed: true));
            importer.Found.Add(Info("a1", "Artist One - First Song", "Artist One"));
            importer.Found.Add(Info("b2", "Artist Two - Second Song", "Artist Two"));
            var state = State();
            var vm = new YouTubeImportViewModel(importer, new HttpClient(), state, initialQuery: "song");
            var (win, view) = Show(vm);
            try
            {
                Assert.True(view.PillStylesLoaded, "Noctis's PillDialog.axaml did not load");
                Assert.True(vm.ToolInstalled);

                // Search: the filled pill field (tone + radius from PillDialog.axaml), focused on
                // open, and the round solid-accent search button.
                var query = view.QueryBox;
                Assert.Contains("pill-field", query.Classes);
                Assert.True(query.IsEffectivelyEnabled);
                var chrome = query.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_BorderElement");
                Assert.Equal(new CornerRadius(999), chrome.CornerRadius);
                Assert.True(PumpUntil(() => query.IsFocused), "search box not focused on open");
                var search = win.GetVisualDescendants().OfType<Button>().Single(b => b.Command == vm.SearchCommand);
                PillDialogHostTests.AssertSolidAccent(search);

                // Format: every format with ffmpeg, M4A first; the choice is remembered.
                Assert.Equal(AudioFormat.All, view.FormatBox.Items.Cast<AudioFormat>());
                Assert.Same(AudioFormat.M4a, vm.SelectedFormat);
                view.FormatBox.SelectedItem = AudioFormat.Mp3High;
                Assert.Same(AudioFormat.Mp3High, vm.SelectedFormat);
                Assert.Equal("mp3-320", State().Format);

                // Results: pill-well cards with a bound Download pill each.
                Assert.False(win.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "SetupPanel").IsVisible);
                Assert.Equal(2, vm.Results.Count);
                Assert.True(PumpUntil(() => win.GetVisualDescendants().OfType<Border>()
                    .Count(b => b.Classes.Contains("yt-row") && b.IsEffectivelyVisible) == 2), "result cards not shown");
                var cards = win.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("yt-row")).ToList();
                Assert.All(cards, c => Assert.Contains("pill-well", c.Classes));
                var downloads = win.GetVisualDescendants().OfType<Button>()
                    .Where(b => vm.Results.Any(r => ReferenceEquals(b.Command, r.DownloadCommand))).ToList();
                Assert.Equal(2, downloads.Count);
                Assert.All(downloads, b => Assert.Contains("pill-secondary", b.Classes));

                // Download the first: in the chosen format, into the library, the card takes the
                // accent ring, and it heads the recent downloads.
                Assert.False(vm.HasRecent);
                downloads[0].Command!.Execute(null);
                var row = vm.Results[0];
                Assert.True(PumpUntil(() => row.IsDone), "download never finished");
                Assert.Equal(new[] { AudioFormat.Mp3High }, importer.Imports);
                Assert.Equal("In your library", row.StatusText);
                Assert.True(PumpUntil(() => AccentTestHarness.ColorOf(cards[0].BorderBrush) == Color.Parse("#E74856")),
                    $"ring {AccentTestHarness.ColorOf(cards[0].BorderBrush)}");
                Assert.False(downloads[0].IsVisible);
                Assert.True(PumpUntil(() => vm.HasRecent && view.RecentList.IsEffectivelyVisible), "recent list not shown");
                var recent = Assert.Single(vm.Recent);
                Assert.Equal(("C:/Music/YouTube/a1.m4a", true), (recent.FilePath, recent.InLibrary));
                Assert.Single(State().Recent); // remembered for the next session

                // Esc closes the window.
                var closed = 0;
                win.Closed += (_, _) => closed++;
                win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                Assert.True(PumpUntil(() => closed > 0, 2000));
            }
            finally { Close(win); }
        });
    }

    [AvaloniaFact]
    public void Esc_InTheViewShownInSettings_LeavesTheMainWindowOpen()
    {
        // Settings shows a plugin's view in place (PluginViewHost) inside Noctis's main window:
        // Esc there must not close that window, only the view's own window.
        EnsureAppStyles();
        var view = new YouTubeImportView(new YouTubeImportViewModel(new FakeImporter(MakeTool(installed: true)), new HttpClient(), State()));
        var host = new ContentControl { Name = "PluginViewHost", Content = view };
        var win = new Window { Width = 1200, Height = 800, Content = new Border { Child = host } };
        win.Show();
        try
        {
            var closed = 0;
            win.Closed += (_, _) => closed++;
            Dispatcher.UIThread.RunJobs();
            view.Focus();
            win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            PumpUntil(() => closed > 0, 300);
            Assert.Equal(0, closed);
            Assert.True(win.IsVisible);
        }
        finally { Close(win); }
    }

    [AvaloniaFact]
    public void WithoutFfmpeg_OnlyM4aIsOffered_EvenWhenMp3WasRemembered()
    {
        EnsureAppStyles();
        var state = State();
        state.SetFormat("mp3-320");
        var vm = new YouTubeImportViewModel(new FakeImporter(MakeTool(installed: true), ffmpeg: false), new HttpClient(), state);
        var (win, view) = Show(vm);
        try
        {
            Assert.Equal(new[] { AudioFormat.M4a }, vm.Formats);
            Assert.Same(AudioFormat.M4a, vm.SelectedFormat);
            Assert.Single(view.FormatBox.Items);
        }
        finally { Close(win); }
    }

    /// <summary>Before the plugin, a file the library import skipped still said "In your library".</summary>
    [AvaloniaFact]
    public void Download_TheLibraryDidNotTake_SaysSavedToFolder()
    {
        var importer = new FakeImporter(MakeTool(installed: true)) { InLibrary = false };
        importer.Found.Add(Info("a1", "A - B", "A"));
        var vm = new YouTubeImportViewModel(importer, new HttpClient(), State(), initialQuery: "b");
        Assert.True(PumpUntil(() => vm.Results.Count == 1));
        vm.Results[0].DownloadCommand.Execute(null);
        Assert.True(PumpUntil(() => vm.Results[0].IsDone));
        Assert.Equal("Saved to folder", vm.Results[0].StatusText);
        Assert.False(Assert.Single(vm.Recent).InLibrary);
    }

    [AvaloniaFact]
    public async Task Folder_ChangeAndReset_AreRemembered()
    {
        var importer = new FakeImporter(MakeTool(installed: true));
        var state = State();
        // The real importer reads the state's folder; the fake mirrors that.
        var vm = new YouTubeImportViewModel(new FolderFollowingImporter(importer, state), new HttpClient(), state);
        Assert.Equal("C:/Music/YouTube", vm.DestinationFolder);
        Assert.False(vm.HasCustomFolder);

        vm.PickFolder = () => Task.FromResult<string?>(@"D:\Downloads\Songs");
        await vm.ChangeFolderCommand.ExecuteAsync(null);
        Assert.Equal(@"D:\Downloads\Songs", vm.DestinationFolder);
        Assert.True(vm.HasCustomFolder);
        Assert.Equal(@"D:\Downloads\Songs", State().DownloadFolder);

        vm.PickFolder = () => Task.FromResult<string?>(null); // picker cancelled: nothing changes
        await vm.ChangeFolderCommand.ExecuteAsync(null);
        Assert.Equal(@"D:\Downloads\Songs", vm.DestinationFolder);

        vm.ResetFolderCommand.Execute(null);
        Assert.Equal("C:/Music/YouTube", vm.DestinationFolder);
        Assert.False(vm.HasCustomFolder);
        Assert.Equal("", State().DownloadFolder);
    }

    private sealed class FolderFollowingImporter(FakeImporter inner, ImportState state) : IYouTubeImporter
    {
        public YtDlpTool Tool => inner.Tool;
        public bool HasFfmpeg => inner.HasFfmpeg;
        public string ResolveDownloadFolder() => state.DownloadFolder is { Length: > 0 } f ? f : inner.ResolveDownloadFolder();
        public Task<List<YouTubeTrackInfo>> SearchAsync(string query, CancellationToken ct) => inner.SearchAsync(query, ct);
        public Task<YouTubeTrackInfo?> ResolveAsync(string urlOrId, CancellationToken ct) => inner.ResolveAsync(urlOrId, ct);
        public Task<YouTubeImportResult> ImportAsync(YouTubeTrackInfo info, AudioFormat format, IProgress<YouTubeImportProgress>? progress, CancellationToken ct)
            => inner.ImportAsync(info, format, progress, ct);
    }

    /// <summary>
    /// Live 10-08: yt-dlp came from PATH (Chocolatey, 2026.07.04), which is never replaced. The
    /// footer offers Update (an own copy, which then wins over PATH) only for a copy it may not
    /// replace and only while a newer release is known.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(true)]   // an outdated copy the plugin may not replace (custom path)
    [InlineData(false)]  // the plugin's own copy, current
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
                tool = MakeTool(installed: false, custom: custom, version: "2026.07.04");
            }
            else tool = MakeTool(installed: true);

            var vm = new YouTubeImportViewModel(new FakeImporter(tool), new HttpClient(), State());
            var (win, _) = Show(vm);
            try
            {
                Assert.True(PumpUntil(() => vm.HasToolVersion), "version note never showed");
                var update = win.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "UpdateToolButton");
                if (foreignOutdated)
                {
                    Assert.True(PumpUntil(() => vm.ShowToolUpdate && update.IsEffectivelyVisible),
                        $"no Update pill (note '{vm.ToolVersionText}')");
                    Assert.Same(vm.InstallToolCommand, update.Command);
                    Assert.Contains("pill-secondary", update.Classes);
                    Assert.Contains("update available", vm.ToolVersionText);
                }
                else
                {
                    PumpUntil(() => false, 200);
                    Assert.False(vm.ShowToolUpdate);
                    Assert.False(update.IsEffectivelyVisible);
                }
            }
            finally { Close(win); }
        });
    }

    [AvaloniaFact]
    public void ToolMissing_ShowsOnlyTheInstallAction()
    {
        EnsureAppStyles();
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var vm = new YouTubeImportViewModel(new FakeImporter(MakeTool(installed: false)), new HttpClient(), State());
            // yt-dlp on this machine's PATH would count as installed: force the missing state.
            vm.ToolInstalled = false;
            var (win, view) = Show(vm);
            try
            {
                var setup = win.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "SetupPanel");
                Assert.True(PumpUntil(() => setup.IsEffectivelyVisible));

                var install = win.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "InstallButton");
                Assert.Same(vm.InstallToolCommand, install.Command);
                Assert.True(install.IsEffectivelyVisible);
                Assert.True(install.IsEffectivelyEnabled);
                PillDialogHostTests.AssertSolidAccent(install);

                // Nothing else competes with it: search is off, no results / empty hint / version.
                Assert.False(view.QueryBox.IsEffectivelyEnabled);
                Assert.False(vm.ShowResults);
                Assert.False(vm.ShowEmpty);
                Assert.False(vm.ShowSkeleton);
                Assert.False(vm.HasToolVersion);

                // Installed: the setup gives way to the empty hint and the search comes alive.
                vm.ToolInstalled = true;
                Assert.True(PumpUntil(() => !setup.IsVisible && vm.ShowEmpty));
                Assert.True(view.QueryBox.IsEffectivelyEnabled);
            }
            finally { Close(win); }
        });
    }

    /// <summary>
    /// Owner 10-29: the play triangle in the coral header tile sat off centre. A right-pointing
    /// triangle looks left-heavy, so its box sits a little right of the tile's centre (about 7%
    /// of its width), vertically centred. Measured from the drawn geometry; with real Skia also
    /// from the white pixels.
    /// </summary>
    [AvaloniaFact]
    public void HeaderPlayGlyph_IsOpticallyCentredInTheTile()
    {
        var (win, view) = Show(new YouTubeImportViewModel(new FakeImporter(MakeTool(installed: true)), new HttpClient(), State()));
        try
        {
            PumpUntil(() => false, 100);
            var glyph = view.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>()
                .Single(p => p.Parent is Border { Width: 56 });
            var tile = (Border)glyph.Parent!;
            var drawn = glyph.RenderedGeometry!.Bounds;
            var topLeft = glyph.TranslatePoint(drawn.TopLeft, tile)!.Value;
            var box = new Rect(topLeft, drawn.Size);
            var shift = box.Center.X - tile.Bounds.Width / 2;
            _o.WriteLine($"geometry box {box}, centre {box.Center}, tile centre {tile.Bounds.Width / 2}, shift {shift:0.00} px = {shift / box.Width:P1} of width");
            Assert.InRange(shift / box.Width, 0.05, 0.09);
            Assert.InRange(box.Center.Y - tile.Bounds.Height / 2, -0.25, 0.25);

            if (HeadlessTestApp.RealRendering)
            {
                using var frame = win.CaptureRenderedFrame()!;
                var origin = tile.TranslatePoint(default, win)!.Value;
                var (sx, sy, n) = (0.0, 0.0, 0);
                var px = new byte[4 * 56 * 56];
                var pin = System.Runtime.InteropServices.GCHandle.Alloc(px, System.Runtime.InteropServices.GCHandleType.Pinned);
                try { frame.CopyPixels(new PixelRect((int)Math.Round(origin.X), (int)Math.Round(origin.Y), 56, 56), pin.AddrOfPinnedObject(), px.Length, 4 * 56); }
                finally { pin.Free(); }
                for (var y = 0; y < 56; y++)
                    for (var x = 0; x < 56; x++)
                    {
                        var i = 4 * (y * 56 + x);
                        if (px[i] < 235 || px[i + 1] < 235 || px[i + 2] < 235) continue; // white only, not coral
                        sx += x + 0.5; sy += y + 0.5; n++;
                    }
                // A right-pointing triangle's area centroid is a third of its width from the base.
                var expectedX = box.Left + box.Width / 3;
                _o.WriteLine($"white pixels {n}, centroid ({sx / n:0.00}, {sy / n:0.00}), triangle centroid ({expectedX:0.00}, {box.Center.Y:0.00}), tile centre 28");
                Assert.InRange(sx / n - expectedX, -0.3, 0.3);
                Assert.InRange(sy / n - box.Center.Y, -0.3, 0.3);
            }
        }
        finally { Close(win); }
    }

    /// <summary>Real Skia only: PNGs of the results and setup states, for eyeballing the layout.</summary>
    [AvaloniaFact]
    public void Probe_SavesTheWindowStates()
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        EnsureAppStyles();
        var dir = Environment.GetEnvironmentVariable("NOCTIS_YT_PLUGIN_SHOTS") is { Length: > 0 } d ? d : Path.Combine(Path.GetTempPath(), "noctis-youtube-plugin-shots");
        Directory.CreateDirectory(dir);
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var importer = new FakeImporter(MakeTool(installed: true));
            importer.Found.Add(Info("a1", "Artist One - First Song (Official Video)", "Artist One"));
            importer.Found.Add(Info("b2", "Artist Two - Second Song", "Artist Two"));
            importer.Found.Add(Info("c3", "Artist Three - Third Song (Live)", "Artist Three"));
            var state = State();
            state.AddRecent(new RecentDownload("Older Song", "Older Artist", "C:/Music/YouTube/old.m4a", DateTimeOffset.Now.AddHours(-3), true));
            var vm = new YouTubeImportViewModel(importer, new HttpClient(), state, initialQuery: "song");
            var (win, _) = Show(vm);
            try
            {
                PumpUntil(() => vm.Results.Count == 3);
                vm.Results[0].DownloadCommand.Execute(null);
                vm.Results[1].IsBusy = true;
                vm.Results[1].Progress = 0.42;
                vm.Results[1].StatusText = "Downloading";
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
            finally { Close(win); }
        });
    }

    // ── Importer ─────────────────────────────────────────────────────────────

    [Fact]
    public void DefaultFolder_IsYouTube_InTheFolderThatHoldsEveryMusicFolder()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Owner 10-08: one music folder per artist; downloads go next to them, not inside the first.
        Assert.Equal(@"B:\ALAC\YouTube", YouTubeImporter.DefaultDownloadFolder(new[] { @"B:\ALAC\Taylor Swift", @"B:\ALAC\Drake", @"B:\ALAC\Kanye West\" }));
        Assert.Equal(@"C:\Music\YouTube", YouTubeImporter.DefaultDownloadFolder(new[] { @"C:\Music" }));
        // Only the drive in common: the first folder, as before.
        Assert.Equal(@"B:\Rock\YouTube", YouTubeImporter.DefaultDownloadFolder(new[] { @"B:\Rock", @"B:\Jazz" }));
        Assert.Equal(@"C:\Music\YouTube", YouTubeImporter.DefaultDownloadFolder(new[] { @"C:\Music", @"D:\Songs" }));
        Assert.Equal(@"\\nas\music\YouTube", YouTubeImporter.DefaultDownloadFolder(new[] { @"\\nas\music\a", @"\\nas\music\b" }));
        Assert.Equal(string.Empty, YouTubeImporter.DefaultDownloadFolder(new[] { "", "  " }));
    }

    private sealed class FakeLibraryImport : ILibraryImport
    {
        public List<string> Folders = new();
        public IReadOnlyList<string> MusicFolders => Folders;
        public bool Accept = true;
        public readonly List<IReadOnlyList<string>> Calls = new();
        public Task<IReadOnlyList<TrackInfo>> ImportFilesAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
        {
            Calls.Add(paths.ToList());
            IReadOnlyList<TrackInfo> result = Accept
                ? paths.Select(p => new TrackInfo(Guid.NewGuid().ToString("D"), "t", "a", "al", "a", TimeSpan.Zero, 0, "", 0, p, false, 0, 0)).ToList()
                : Array.Empty<TrackInfo>();
            return Task.FromResult(result);
        }
    }

    /// <summary>yt-dlp faked to write the file the args ask for, with the JSON for "vid".</summary>
    private YtDlpTool DownloadingTool(List<IReadOnlyList<string>> runs, string ext = "m4a")
    {
        var tool = MakeTool(installed: true);
        tool.Runner = (_, args, onLine, _) =>
        {
            if (args.Count == 1 && args[0] == "--version") return Task.FromResult((0, "2026.09.01\n", string.Empty));
            runs.Add(args);
            var o = args.ToList().IndexOf("-o");
            if (o >= 0)
            {
                File.WriteAllBytes(args[o + 1].Replace("%(id)s", "vid").Replace("%(ext)s", ext), new byte[] { 1, 2, 3 });
                onLine?.Invoke("[download] 100.0% of 3B");
                return Task.FromResult((0, string.Empty, string.Empty));
            }
            return Task.FromResult((0, """{"id":"vid","title":"Artist - Song (Official Video)","artist":"Artist","track":"Song"}""" + "\n", string.Empty));
        };
        return tool;
    }

    [Fact]
    public async Task Import_DownloadsIntoTheFolder_NamesTheFile_AndAddsItThroughTheApi()
    {
        var runs = new List<IReadOnlyList<string>>();
        var library = new FakeLibraryImport { Folders = { Path.Combine(_root, "Music") } };
        var importer = new YouTubeImporter(DownloadingTool(runs), library, new HttpClient(new NoNetwork()), () => "", () => null);

        var result = await importer.ImportAsync(Info("vid", "Artist - Song (Official Video)", "Artist"), AudioFormat.M4a, null, CancellationToken.None);

        var expected = Path.Combine(_root, "Music", "YouTube", "Artist - Song.m4a");
        Assert.Equal(expected, result.FilePath);
        Assert.True(File.Exists(expected));
        Assert.True(result.AddedToLibrary);
        Assert.Equal(new[] { expected }, Assert.Single(library.Calls));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(expected)!)); // the scratch folder is gone
        Assert.Contains("bestaudio[ext=m4a]/bestaudio/best", runs[^1]);
    }

    [Fact]
    public async Task Import_LibraryRefusesTheFile_ReportsNotAdded()
    {
        var library = new FakeLibraryImport { Accept = false, Folders = { Path.Combine(_root, "Music") } };
        var importer = new YouTubeImporter(DownloadingTool(new()), library, new HttpClient(new NoNetwork()), () => "", () => null);
        var result = await importer.ImportAsync(Info("vid", "A - B", "A"), AudioFormat.M4a, null, CancellationToken.None);
        Assert.False(result.AddedToLibrary);
        Assert.True(File.Exists(result.FilePath));
    }

    [Fact]
    public async Task Import_Mp3WithoutFfmpeg_SaysSo_BeforeRunningAnything()
    {
        var runs = new List<IReadOnlyList<string>>();
        var library = new FakeLibraryImport { Folders = { Path.Combine(_root, "Music") } };
        var importer = new YouTubeImporter(DownloadingTool(runs), library, new HttpClient(new NoNetwork()), () => "", () => null);
        Assert.False(importer.HasFfmpeg);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportAsync(Info("vid", "A - B", "A"), AudioFormat.Mp3High, null, CancellationToken.None));

        Assert.Contains("ffmpeg", ex.Message);
        Assert.Empty(runs);
        Assert.Empty(library.Calls);
    }

    [Fact]
    public async Task Import_Mp3WithFfmpeg_ConvertsAt320_IntoTheChosenFolder()
    {
        var runs = new List<IReadOnlyList<string>>();
        var custom = Path.Combine(_root, "Chosen");
        var ffmpeg = Path.Combine(_root, "ff", "ffmpeg.exe");
        var importer = new YouTubeImporter(DownloadingTool(runs, "mp3"), new FakeLibraryImport(), new HttpClient(new NoNetwork()), () => custom, () => ffmpeg);

        var result = await importer.ImportAsync(Info("vid", "A - B", "A"), AudioFormat.Mp3High, null, CancellationToken.None);

        Assert.Equal(Path.Combine(custom, "Artist - Song.mp3"), result.FilePath); // tags from the full record yt-dlp returned
        var args = runs[^1].ToList();
        Assert.Equal("mp3", args[args.IndexOf("--audio-format") + 1]);
        Assert.Equal("320K", args[args.IndexOf("--audio-quality") + 1]);
        Assert.Equal(Path.GetDirectoryName(ffmpeg), args[args.IndexOf("--ffmpeg-location") + 1]);
    }

    [Fact]
    public async Task Import_NoFolderAtAll_SaysAddAMusicFolder()
    {
        var importer = new YouTubeImporter(DownloadingTool(new()), new FakeLibraryImport(), new HttpClient(new NoNetwork()), () => "", () => null);
        Assert.Equal("", importer.ResolveDownloadFolder());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportAsync(Info("vid", "A - B", "A"), AudioFormat.M4a, null, CancellationToken.None));
        Assert.Contains("music folder", ex.Message);
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }

    // ── State ────────────────────────────────────────────────────────────────

    [Fact]
    public void State_KeepsTwentyRecent_NewestFirst_AndSurvivesABrokenFile()
    {
        var state = State();
        for (var i = 0; i < 25; i++) state.AddRecent(new RecentDownload($"t{i}", "a", $"C:/x/{i}.m4a", DateTimeOffset.Now, true));
        state.AddRecent(new RecentDownload("again", "a", "C:/x/24.m4a", DateTimeOffset.Now, true)); // same file: moves up, no duplicate
        var reread = State();
        Assert.Equal(ImportState.MaxRecent, reread.Recent.Count);
        Assert.Equal("again", reread.Recent[0].Title);
        Assert.Single(reread.Recent, r => r.FilePath == "C:/x/24.m4a");

        File.WriteAllText(Path.Combine(_root, "data", ImportState.FileName), "{ not json");
        var broken = State();
        Assert.Empty(broken.Recent);
        Assert.Equal("", broken.DownloadFolder);
        Assert.Same(AudioFormat.Default, AudioFormat.FromKey(broken.Format));
    }

    [Fact]
    public void RecentDetail_ReadsLikeAListLine()
    {
        var now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("Artist · In your library · 5 min ago", YouTubeImportView.RecentDetail(new RecentDownload("t", "Artist", "p", now.AddMinutes(-5), true), now));
        Assert.Equal("Saved to folder · just now", YouTubeImportView.RecentDetail(new RecentDownload("t", "", "p", now, false), now));
    }

    // ── The plugin ───────────────────────────────────────────────────────────

    [Fact]
    public void ToolsFolder_IsTheAppsOwn_WhenTheDataFolderIsWhereNoctisPutsIt()
    {
        var data = Path.Combine(_root, "Noctis");
        Assert.Equal(Path.Combine(data, "tools"), YouTubeImportPlugin.ToolsDirectory(Path.Combine(data, "plugin-data", YouTubeImportPlugin.Id)));
        var odd = Path.Combine(_root, "elsewhere");
        Assert.Equal(Path.Combine(odd, "tools"), YouTubeImportPlugin.ToolsDirectory(odd));
    }

    [Fact]
    public void FirstRun_CarriesOverTheFolderSetInNoctis_OnceOnly()
    {
        // Owner 10-29: the built-in version's custom folder (C:\Users\...\Downloads\) was dropped.
        var appData = Path.Combine(_root, "Noctis");
        var pluginData = Path.Combine(appData, "plugin-data", YouTubeImportPlugin.Id);
        var old = Path.Combine(_root, "My Downloads");
        Directory.CreateDirectory(appData);
        File.WriteAllText(Path.Combine(appData, "settings.json"),
            System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["youTubeDownloadFolder"] = old + "  ", ["ytDlpPath"] = @"C:\tools\yt-dlp.exe" }));

        Assert.Equal(old, LegacyAppSettings.Read(pluginData, LegacyAppSettings.DownloadFolderKey));
        Assert.Equal(@"C:\tools\yt-dlp.exe", LegacyAppSettings.Read(pluginData, LegacyAppSettings.YtDlpPathKey));
        Assert.Equal("", LegacyAppSettings.Read(Path.Combine(_root, "elsewhere"), LegacyAppSettings.DownloadFolderKey));

        Func<string?> legacy = () => LegacyAppSettings.Read(pluginData, LegacyAppSettings.DownloadFolderKey);
        var first = new ImportState(pluginData, legacy);
        Assert.Equal(old, first.DownloadFolder);

        // Reset to the default in the window: the old folder does not come back next time.
        first.SetDownloadFolder("");
        Assert.Equal("", new ImportState(pluginData, legacy).DownloadFolder);
    }

    [Fact]
    public void Manifest_MatchesThePlugin_AndAsksForTheMinimum()
    {
        var m = PluginManifest.TryLoad(PluginSandbox.RepoFile("plugins/Noctis.Plugins.YouTubeImport"))!;
        var plugin = new YouTubeImportPlugin();
        Assert.Equal((YouTubeImportPlugin.Id, "Add from YouTube", "1.0.1", "Noctis"), (m.Id, m.Name, m.Version, m.Author));
        Assert.Equal((plugin.Info.Id, plugin.Info.Name, plugin.Info.Version, plugin.Info.Description), (m.Id, m.Name, m.Version, m.Description));
        Assert.Equal("Save audio from a YouTube link to your library.", m.Description);
        Assert.Equal(new[] { "library.import", "network" }, m.Permissions);
        Assert.Equal("1.5", m.ApiVersion);
        Assert.Empty(m.Warnings);
        Assert.Null(m.CheckCompatibility("1.6.6"));
        Assert.NotNull(m.CheckCompatibility("1.6.5"));
        Assert.True(File.Exists(PluginSandbox.RepoFile("plugins/Noctis.Plugins.YouTubeImport/icon.png")));
    }

    [AvaloniaFact]
    public async Task InProcess_RunsWithItsPermissions_OpensItsWindow_AndImportsThroughTheHost()
    {
        using var box = new PluginSandbox();
        var library = new FakeLibraryService { ImportAddsTracks = true };
        box.Settings.MusicFolders = new List<string> { Path.Combine(box.Root, "Music") };
        box.Approve((YouTubeImportPlugin.Id, new[] { "library.import", "network" }));
        var host = box.NewHost(library: library, appVersion: "1.6.6");
        var manifest = File.ReadAllText(PluginSandbox.RepoFile("plugins/Noctis.Plugins.YouTubeImport/plugin.json"));
        var instance = new YouTubeImportPlugin();
        var plugin = host.AddInProcess(box.WriteFolder(YouTubeImportPlugin.Id, manifest), PluginManifest.Parse(manifest), () => instance);

        Assert.Equal(PluginStatus.Running, plugin.Status);
        var view = Assert.Single(plugin.Views);
        Assert.Equal("Add from YouTube", view.Name);
        var control = Assert.IsType<YouTubeImportView>(view.Provider.CreateView());
        Assert.Equal(Path.Combine(box.Root, "Music", "YouTube"), control.ViewModel.DestinationFolder);

        // The importer the plugin built goes through the host's library.import.
        var song = Path.Combine(box.Root, "Music", "YouTube", "A - B.m4a");
        Directory.CreateDirectory(Path.GetDirectoryName(song)!);
        File.WriteAllBytes(song, new byte[] { 1 });
        var tracks = await plugin.Adapter!.LibraryImport.ImportFilesAsync(new[] { song });
        Assert.Equal(song, Assert.Single(tracks).FilePath);
        Assert.Single(library.ImportCalls);

        host.SetEnabled(plugin, false);
        Assert.Equal(PluginStatus.Disabled, plugin.Status);
        Assert.Empty(plugin.Views);
    }

    /// <summary>The zip PluginPackage.targets writes is what users install (D:\NoctisMockups\plugins copy too).</summary>
    [AvaloniaFact]
    public void ReleaseZip_InstallsFromFile_AndRuns()
    {
        var zip = PluginSandbox.RepoFile("plugins/Noctis.Plugins.YouTubeImport/bin/Release/Noctis.Plugins.YouTubeImport-1.0.0.zip");
        if (!File.Exists(zip)) Assert.Skip("build it first: dotnet build plugins/Noctis.Plugins.YouTubeImport -c Release");

        using (var archive = ZipFile.OpenRead(zip))
        {
            var names = archive.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Assert.Equal(new[] { "Noctis.Plugins.YouTubeImport.deps.json", "Noctis.Plugins.YouTubeImport.dll", "icon.png", "plugin.json" }, names);
        }

        using var box = new PluginSandbox();
        box.Settings.CommunityPluginsEnabled = true;
        var host = box.NewHost(library: new FakeLibraryService(), appVersion: "1.6.6");
        host.LoadAll();
        var result = host.InstallPackage(zip, allowUpdate: false);
        Assert.Equal(PluginInstallOutcome.Installed, result.Outcome);
        var plugin = result.Plugin!;
        Assert.Equal(("Add from YouTube", "1.0.0", "Noctis"), (plugin.Name, plugin.Version, plugin.Manifest?.Author));
        Assert.Equal(PluginStatus.Disabled, plugin.Status); // waits for the user's approval

        host.SetEnabled(plugin, true);
        Assert.Equal(PluginStatus.Running, plugin.Status);
        var view = Assert.Single(plugin.Views);
        Assert.Equal("Add from YouTube", view.Name);
        Assert.NotNull(view.Provider.CreateView()); // TagLib# and the MVVM toolkit resolve to the app's copies
        Assert.True(host.Remove(plugin, deleteData: true));
    }
}
