using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.YouTube;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-08: the Lyrics Background Video pop-up as a rounded pill dialog, plus the bugs
/// found on the way (download/cancel races, Remove/Choose while a download runs, rows that
/// went stale, a song row that said "default" while it plays its album's clip). yt-dlp is
/// faked through YtDlpTool's Runner seam; no process, no network.
/// </summary>
public class LyricsBackgroundPickerDialogTests
{
    private const string Link = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";
    private static readonly byte[] DownloadedBytes = { 0x59, 0x54, 0x31 }; // "YT1"
    private static readonly Color PillFill = Color.Parse("#1CFFFFFF");

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    /// <summary>
    /// yt-dlp at its edges: "--version" answers, a download writes "%(id)s.%(ext)s" into the
    /// scratch folder it is handed. With <see cref="Block"/> it waits for <see cref="Gate"/> or
    /// the token, the way the real run waits on the process; a cancel throws as
    /// RunProcessAsync's ReadLineAsync(ct) does.
    /// </summary>
    private sealed class FakeYtDlp : IDisposable
    {
        public readonly YtDlpTool Tool;
        public readonly string Root;
        public bool Block;
        public readonly TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<string, CancellationToken, Task<(int, string, string)>>? OnDownload;
        public CancellationToken LastToken;
        public string? LastOutputFile;
        public int Downloads;
        /// <summary>The user-set yt-dlp path (a copy Noctis may not replace).</summary>
        public readonly string ExePath;

        public FakeYtDlp()
        {
            Root = Path.Combine(Path.GetTempPath(), "NoctisTests", "ytdlp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            var exe = ExePath = Path.Combine(Root, "yt-dlp.exe");
            File.WriteAllBytes(exe, new byte[] { 0 });
            Tool = new YtDlpTool(new HttpClient(), Root, () => exe)
            {
                JsRuntimes = (true, false),
                LatestVersionFetcher = _ => Task.FromResult<string?>(null),
                Updater = _ => Task.CompletedTask,
            };
            Tool.Runner = RunAsync;
        }

        private async Task<(int, string, string)> RunAsync(string exe, IReadOnlyList<string> args, Action<string>? onLine, CancellationToken ct)
        {
            if (args.Contains("--version")) return (0, "2026.08.19\n", string.Empty);
            var list = args.ToList();
            var file = list[list.IndexOf("-o") + 1].Replace("%(id)s.%(ext)s", "dQw4w9WgXcQ.mp4");
            LastOutputFile = file;
            LastToken = ct;
            Downloads++;
            Started.TrySetResult();
            if (OnDownload != null) return await OnDownload(file, ct);
            await File.WriteAllBytesAsync(file, DownloadedBytes, CancellationToken.None);
            onLine?.Invoke("[download]  42.0% of 10.00MiB at 1.00MiB/s ETA 00:05");
            if (Block)
            {
                await Task.WhenAny(Gate.Task, Task.Delay(Timeout.Infinite, ct));
                ct.ThrowIfCancellationRequested();
            }
            return (0, string.Empty, string.Empty);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch { }
        }
    }

    /// <summary>A library with one album of two songs, a settings model on a temp root.</summary>
    private sealed class Fixture : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "NoctisTests", "lbg-" + Guid.NewGuid().ToString("N"));
        public readonly FakeLibraryService Library = new();
        public readonly SettingsViewModel Settings;
        public readonly Album Album;
        public readonly Track SongA;
        public readonly Track SongB;

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            var albumId = Guid.NewGuid();
            SongA = new Track { Id = Guid.NewGuid(), Title = "Soy Peor", Artist = "Bad Bunny", Album = "Soy Peor - Single", AlbumId = albumId };
            SongB = new Track { Id = Guid.NewGuid(), Title = "Diles", Artist = "Bad Bunny", Album = "Soy Peor - Single", AlbumId = albumId };
            Library.TrackList.AddRange(new[] { SongA, SongB });
            Album = new Album { Id = albumId, Name = "Soy Peor - Single", Artist = "Bad Bunny", Tracks = new List<Track> { SongA, SongB } };
            ((List<Album>)Library.Albums).Add(Album);
            Settings = new SettingsViewModel(new PersistenceService(Root), Library, new NoOpPlayHistory());
        }

        public string Clip(string name, byte[]? bytes = null)
        {
            var path = Path.Combine(Root, name);
            File.WriteAllBytes(path, bytes ?? new byte[] { 1, 2, 3 });
            return path;
        }

        public void Dispose()
        {
            foreach (var key in Settings.LyricsBackgroundOverrideKeys.ToList())
                Settings.ClearLyricsBackgroundOverride(key);
            if (Settings.HasLyricsBackgroundMedia) Settings.ClearLyricsBackgroundMediaCommand.Execute(null);
            try { Directory.Delete(Root, true); } catch { }
        }
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

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int budgetMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < budgetMs)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    /// <summary>The view model under test, its YouTube scratch folder inside the fixture.</summary>
    private static LyricsBackgroundPickerViewModel NewVm(Fixture f, FakeYtDlp? yt = null, Func<Task<string?>>? pick = null) =>
        new(f.Settings, f.Library, yt?.Tool)
        {
            ScratchRoot = Path.Combine(f.Root, "scratch"),
            PickFile = pick,
        };

    // ── Bugs: download / cancel races ──

    /// <summary>Bug: Remove on a row whose YouTube download was still running did not stop
    /// it; the download then landed on the row the user had just removed, so the song had its
    /// own video again while the list (row gone, "nothing has its own video") said otherwise.</summary>
    [AvaloniaFact]
    public async Task Remove_WhileThatRowsDownloadRuns_CancelsIt_SoTheRemoveSticks()
    {
        using var f = new Fixture();
        using var yt = new FakeYtDlp { Block = true };
        var key = LyricsBackgroundOverrides.KeyForTrack(f.SongA);
        await f.Settings.SetLyricsBackgroundOverrideAsync(key, f.Clip("own.mp4"));
        var vm = NewVm(f, yt);
        var row = Assert.Single(vm.Results);

        vm.YouTubeForItemCommand.Execute(row);
        vm.YouTubeUrl = Link;
        var run = vm.DownloadFromYouTubeCommand.ExecuteAsync(null);
        await yt.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        vm.ClearForItemCommand.Execute(row);
        yt.Gate.TrySetResult(); // yt-dlp would have finished next
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(yt.LastToken.IsCancellationRequested, "Remove left the download running");
        Assert.False(f.Settings.HasLyricsBackgroundOverride(key), "the finished download undid the Remove");
        Assert.DoesNotContain(vm.Results, r => r.Key == key);
        Assert.True(vm.ShowPrompt);
    }

    /// <summary>Bug: the same for the default clip — Remove during its download, then the
    /// download set it again.</summary>
    [AvaloniaFact]
    public async Task RemoveDefault_WhileItsDownloadRuns_CancelsIt()
    {
        using var f = new Fixture();
        using var yt = new FakeYtDlp { Block = true };
        await f.Settings.SetLyricsBackgroundMediaAsync(f.Clip("default.mp4"));
        var vm = NewVm(f, yt);
        Assert.True(vm.HasDefaultVideo);

        vm.YouTubeForDefaultCommand.Execute(null);
        vm.YouTubeUrl = Link;
        var run = vm.DownloadFromYouTubeCommand.ExecuteAsync(null);
        await yt.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        vm.ClearDefaultCommand.Execute(null);
        yt.Gate.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(f.Settings.HasLyricsBackgroundMedia, "the finished download undid the Remove");
        Assert.False(vm.HasDefaultVideo);
    }

    /// <summary>Bug: a file chosen for a row while its YouTube download ran was overwritten
    /// when the (older) download finished. The user's latest choice must win.</summary>
    [AvaloniaFact]
    public async Task ChooseFile_WhileThatRowsDownloadRuns_ThePickWins()
    {
        using var f = new Fixture();
        using var yt = new FakeYtDlp { Block = true };
        var key = LyricsBackgroundOverrides.KeyForTrack(f.SongA);
        await f.Settings.SetLyricsBackgroundOverrideAsync(key, f.Clip("own.mp4"));
        var picked = f.Clip("picked.mp4", new byte[] { 0x50, 0x49, 0x43, 0x4B });
        var vm = NewVm(f, yt, () => Task.FromResult<string?>(picked));
        var row = Assert.Single(vm.Results);

        vm.YouTubeForItemCommand.Execute(row);
        vm.YouTubeUrl = Link;
        var run = vm.DownloadFromYouTubeCommand.ExecuteAsync(null);
        await yt.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await vm.ChooseForItemCommand.ExecuteAsync(row);
        yt.Gate.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        var stored = f.Settings.GetLyricsBackgroundOverridePath(key);
        Assert.NotNull(stored);
        Assert.Equal(File.ReadAllBytes(picked), File.ReadAllBytes(stored!));
    }

    /// <summary>Bug: Cancel (or closing the dialog) landing as yt-dlp exits 0 — the process
    /// is done, so the kill and the token change nothing — still applied the clip.</summary>
    [AvaloniaFact]
    public async Task Cancel_LandingAsYtDlpFinishes_DoesNotApplyTheClip_AndLeavesNoFile()
    {
        using var f = new Fixture();
        using var yt = new FakeYtDlp();
        var vm = NewVm(f, yt);
        yt.OnDownload = async (file, ct) =>
        {
            await File.WriteAllBytesAsync(file, DownloadedBytes, CancellationToken.None);
            vm.CloseYouTubeCommand.Execute(null); // the user's Cancel, the same moment
            return (0, string.Empty, string.Empty);
        };

        vm.YouTubeForDefaultCommand.Execute(null);
        vm.YouTubeUrl = Link;
        await vm.DownloadFromYouTubeCommand.ExecuteAsync(null);

        Assert.True(yt.LastToken.IsCancellationRequested);
        Assert.False(f.Settings.HasLyricsBackgroundMedia, "Cancel was ignored: the clip was applied");
        Assert.False(vm.HasDefaultVideo);
        Assert.True(await WaitUntilAsync(() => !File.Exists(yt.LastOutputFile!)), "the downloaded file was left behind");
    }

    /// <summary>Bug: clicking YouTube on another row mid-download retargeted the panel (its
    /// title named the new row) while the running download still landed on the first.</summary>
    [AvaloniaFact]
    public async Task YouTubeOnAnotherRow_MidDownload_KeepsThePanelOnTheRowItLandsOn()
    {
        using var f = new Fixture();
        using var yt = new FakeYtDlp { Block = true };
        var keyA = LyricsBackgroundOverrides.KeyForTrack(f.SongA);
        var keyB = LyricsBackgroundOverrides.KeyForTrack(f.SongB);
        await f.Settings.SetLyricsBackgroundOverrideAsync(keyA, f.Clip("a.mp4"));
        await f.Settings.SetLyricsBackgroundOverrideAsync(keyB, f.Clip("b.mp4", new byte[] { 0x42 }));
        var vm = NewVm(f, yt);
        var rowA = vm.Results.Single(r => r.Key == keyA);
        var rowB = vm.Results.Single(r => r.Key == keyB);

        vm.YouTubeForItemCommand.Execute(rowA);
        vm.YouTubeUrl = Link;
        var run = vm.DownloadFromYouTubeCommand.ExecuteAsync(null);
        await yt.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        vm.YouTubeForItemCommand.Execute(rowB);
        Assert.Same(rowA, vm.YouTubeTarget);
        Assert.Equal(rowA.Title, vm.YouTubeTargetLabel);
        Assert.True(vm.Results.Single(r => r.Key == keyA).IsDownloading);
        Assert.Equal(Loc.T("LyricsVideo.RowDownloading"), vm.Results.Single(r => r.Key == keyA).StatusText);
        Assert.False(vm.Results.Single(r => r.Key == keyB).IsDownloading);

        yt.Gate.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DownloadedBytes, File.ReadAllBytes(f.Settings.GetLyricsBackgroundOverridePath(keyA)!));
        Assert.Equal(new byte[] { 0x42 }, File.ReadAllBytes(f.Settings.GetLyricsBackgroundOverridePath(keyB)!));
    }

    /// <summary>Bug: a search typed while a row's download ran rebuilt the rows; the finished
    /// download updated the old (no longer shown) row, so the visible one kept saying "Uses the
    /// default video".</summary>
    [AvaloniaFact]
    public async Task SearchRefreshedMidDownload_TheVisibleRowShowsItsNewVideo()
    {
        using var f = new Fixture();
        using var yt = new FakeYtDlp { Block = true };
        var key = LyricsBackgroundOverrides.KeyForTrack(f.SongA);
        var vm = NewVm(f, yt);
        vm.SearchText = "soy peor";
        await vm.SearchRefresh;
        var row = vm.Results.Single(r => r.Key == key);
        Assert.False(row.HasOwnVideo);

        vm.YouTubeForItemCommand.Execute(row);
        vm.YouTubeUrl = Link;
        var run = vm.DownloadFromYouTubeCommand.ExecuteAsync(null);
        await yt.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        vm.SearchText = "bad bunny";
        await vm.SearchRefresh;
        yt.Gate.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(f.Settings.HasLyricsBackgroundOverride(key));
        var visible = vm.Results.Single(r => r.Key == key);
        Assert.True(visible.HasOwnVideo, "the visible row still says it uses the default");
        Assert.Equal(Loc.T("LyricsBackground.OwnVideo"), visible.StatusText);
    }

    /// <summary>
    /// The run's files are removed even when yt-dlp still holds its output when the cancel
    /// lands (Process.Kill returns before the process has let go of its handles; the tool's
    /// one Directory.Delete then fails and is swallowed). Simulated: the fake keeps the file
    /// open for 300 ms after throwing.
    /// </summary>
    [AvaloniaFact]
    public async Task Cancel_WhileYtDlpStillHoldsItsFile_StillRemovesThePartialDownload()
    {
        using var f = new Fixture();
        using var yt = new FakeYtDlp();
        string? scratch = null;
        yt.OnDownload = async (file, ct) =>
        {
            scratch = Path.GetDirectoryName(file);
            var held = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None);
            held.Write(DownloadedBytes);
            held.Flush();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException)
            {
                _ = Task.Run(async () => { await Task.Delay(300); held.Dispose(); });
                throw;
            }
            return (0, string.Empty, string.Empty);
        };
        var vm = NewVm(f, yt);
        try
        {
            vm.YouTubeForDefaultCommand.Execute(null);
            vm.YouTubeUrl = Link;
            var run = vm.DownloadFromYouTubeCommand.ExecuteAsync(null);
            await yt.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            vm.CloseYouTubeCommand.Execute(null);
            await run.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(f.Settings.HasLyricsBackgroundMedia);
            Assert.True(await WaitUntilAsync(() => !Directory.Exists(scratch!), 4000),
                $"partial download left behind in {scratch}");
        }
        finally
        {
            await Task.Delay(400);
            try { if (scratch != null && Directory.Exists(scratch)) Directory.Delete(scratch, true); } catch { }
        }
    }

    // ── Bug: album clip vs its songs ──

    /// <summary>Bug: a song whose album has its own clip said "Uses the default video", but
    /// the lyrics page plays the album's clip for it (PlayerViewModel.ResolveLyricsBackgroundFor:
    /// song, then album, then default). Removing the album's clip brings the default back.</summary>
    [AvaloniaFact]
    public async Task SongRow_WhoseAlbumHasAVideo_SaysItPlaysTheAlbumsVideo()
    {
        using var f = new Fixture();
        var albumKey = LyricsBackgroundOverrides.KeyForAlbum(f.Album);
        await f.Settings.SetLyricsBackgroundOverrideAsync(albumKey, f.Clip("album.mp4"));
        var vm = NewVm(f);
        vm.SearchText = "soy peor";
        await vm.SearchRefresh;

        var albumRow = vm.Results.Single(r => r.IsAlbum);
        var song = vm.Results.Single(r => r.Key == LyricsBackgroundOverrides.KeyForTrack(f.SongA));
        Assert.True(albumRow.HasOwnVideo);
        Assert.Equal(Loc.T("LyricsVideo.AlbumBy", "Bad Bunny"), albumRow.Subtitle);
        Assert.NotEqual(Loc.T("LyricsBackground.UsesDefault"), song.StatusText);
        Assert.Equal(Loc.T("LyricsVideo.UsesAlbumVideo"), song.StatusText);

        vm.ClearForItemCommand.Execute(albumRow);
        Assert.Equal(Loc.T("LyricsBackground.UsesDefault"), song.StatusText);
    }

    // ── Dialog ──

    /// <summary>Bug (AUDIT S26): Esc closed the dialog without cancelling a running YouTube
    /// download, which then applied the clip behind the closed dialog.</summary>
    [AvaloniaFact]
    public void Escape_MidDownload_CancelsYtDlp_AndCloses()
    {
        EnsureAppStyles();
        using var f = new Fixture();
        using var yt = new FakeYtDlp { Block = true };
        var vm = NewVm(f, yt);
        var win = new LyricsBackgroundPickerDialog { DataContext = vm, Width = 1000, Height = 760 };
        var closed = 0;
        win.Closed += (_, _) => closed++;
        win.Show();
        try
        {
            Assert.True(PumpUntil(() => win.IsVisible));
            vm.YouTubeForDefaultCommand.Execute(null);
            vm.YouTubeUrl = Link;
            _ = vm.DownloadFromYouTubeCommand.ExecuteAsync(null);
            Assert.True(PumpUntil(() => yt.Started.Task.IsCompleted), "download never started");

            win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.True(PumpUntil(() => yt.LastToken.IsCancellationRequested, 1000), "Esc left yt-dlp running");
            Assert.True(PumpUntil(() => closed > 0 && !vm.IsDownloading, 3000), "dialog never closed");
            Assert.False(f.Settings.HasLyricsBackgroundMedia);
        }
        finally { if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); } }
    }

    /// <summary>Alt+F4 (a plain Window.Close) mid-download cancels it the same way.</summary>
    [AvaloniaFact]
    public void ClosingTheWindow_MidDownload_CancelsYtDlp()
    {
        EnsureAppStyles();
        using var f = new Fixture();
        using var yt = new FakeYtDlp { Block = true };
        var vm = NewVm(f, yt);
        var win = new LyricsBackgroundPickerDialog { DataContext = vm, Width = 1000, Height = 760 };
        win.Show();
        try
        {
            vm.YouTubeForDefaultCommand.Execute(null);
            vm.YouTubeUrl = Link;
            _ = vm.DownloadFromYouTubeCommand.ExecuteAsync(null);
            Assert.True(PumpUntil(() => yt.Started.Task.IsCompleted));
            win.Close();
            Assert.True(yt.LastToken.IsCancellationRequested, "closing the window left yt-dlp running");
            Assert.True(PumpUntil(() => !vm.IsDownloading && !win.IsVisible));
            Assert.Equal(Loc.T("LyricsVideo.Cancelled"), vm.YouTubeStatus);
        }
        finally { if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); } }
    }

    /// <summary>
    /// Owner 10-08: the pop-up in the rounded pill shell, like the other converted pop-ups —
    /// a filled pill search with no outline at rest, rows with one text action (plus a round
    /// YouTube pill and a quiet Remove) instead of three buttons, a YouTube panel that folds out
    /// with the shared Glide and holds a pill link, a pill quality box and a solid accent
    /// Download, Close in the footer, Esc animating out once.
    /// </summary>
    [AvaloniaFact]
    public async Task Dialog_InPillHost_FilledPillControls_CondensedRows()
    {
        EnsureAppStyles();
        using var f = new Fixture();
        using var yt = new FakeYtDlp();
        await f.Settings.SetLyricsBackgroundOverrideAsync(LyricsBackgroundOverrides.KeyForTrack(f.SongA), f.Clip("own.mp4"));
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var vm = NewVm(f, yt);
            var win = new LyricsBackgroundPickerDialog { DataContext = vm, RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
            var closed = 0;
            win.Closed += (_, _) => closed++;
            win.Show();
            try
            {
                var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
                Assert.True(PumpUntil(() => host.Card is { Opacity: > 0.999 } && host.BackdropLayer!.Opacity > 0.999),
                    "open animation never settled");
                Assert.Equal(new CornerRadius(30), host.CornerRadius);

                var all = win.GetVisualDescendants().ToList();
                Assert.DoesNotContain(all.OfType<Button>(), b =>
                    b.Classes.Contains("accent-btn") || b.Classes.Contains("row-action") || b.Classes.Contains("dialog-close"));

                // Search: a filled pill, no outline at rest (nothing takes focus on open).
                var search = win.FindControl<TextBox>("SearchBox")!;
                Assert.Contains("pill-field", search.Classes);
                Assert.False(search.IsFocused);
                var chrome = search.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_BorderElement");
                Assert.Equal(PillFill, AccentTestHarness.ColorOf(chrome.Background));
                Assert.Equal(0, AccentTestHarness.ColorOf(chrome.BorderBrush).A);

                Assert.Contains("pill-well", win.FindControl<Border>("DefaultWell")!.Classes);

                // One row: one text action ("Change", it has a clip), a round YouTube pill, a quiet Remove.
                var list = win.FindControl<ItemsControl>("ResultsList")!;
                Assert.True(PumpUntil(() => list.GetRealizedContainers().Count() == 1));
                var rowButtons = list.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToList();
                Assert.Equal(3, rowButtons.Count);
                var text = Assert.Single(rowButtons, b => b.Content is string);
                Assert.Equal(Loc.T("LyricsVideo.Change"), text.Content);
                Assert.Contains("pill-secondary", text.Classes);
                var remove = rowButtons.Single(b => ReferenceEquals(b.Command, vm.ClearForItemCommand));
                Assert.Equal(0, AccentTestHarness.ColorOf(remove.Background).A); // no fill until hovered
                Assert.Contains(list.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Loc.T("LyricsBackground.OwnVideo"));

                // Close in the footer: the quiet pill the other pill pop-ups use.
                var close = win.FindControl<Button>("CloseButton")!;
                Assert.Contains("pill-secondary", close.Classes);
                Assert.Same(vm.CloseCommand, close.Command);
                Assert.Equal(Loc.T("Common.Close"), close.Content);

                // The card keeps one height (no jumping while a search fills the list) and
                // fits a short window.
                var card = win.FindControl<Grid>("CardRoot")!;
                Assert.Equal(LyricsBackgroundPickerDialog.PreferredCardHeight, card.Height);
                win.Height = 560;
                Assert.True(PumpUntil(() => card.Height == 560 - 48), $"card height {card.Height} on a 560 px window");
                win.Height = 820;
                Assert.True(PumpUntil(() => card.Height == LyricsBackgroundPickerDialog.PreferredCardHeight));

                // YouTube panel: folded; the shared Glide opens it; pill link + pill quality
                // (no white outline) + solid accent Download; the link takes the caret.
                var panel = win.FindControl<CollapsibleContent>("YouTubePanel")!;
                Assert.Equal(CollapsibleMotion.Glide, panel.Motion);
                Assert.False(panel.IsVisible);
                vm.YouTubeForDefaultCommand.Execute(null);
                Assert.True(PumpUntil(() => panel.IsVisible && panel.Reveal > 0.999), "the YouTube panel never opened");
                var link = win.FindControl<TextBox>("LinkBox")!;
                Assert.Contains("pill-field", link.Classes);
                var quality = win.FindControl<ComboBox>("QualityBox")!;
                Assert.Contains("pill-field", quality.Classes);
                var qualityChrome = quality.GetVisualDescendants().OfType<Border>().First(b => b.Name == "Background");
                Assert.Equal(0, AccentTestHarness.ColorOf(qualityChrome.BorderBrush).A);
                Assert.Equal(Loc.T("LyricsVideo.BestQuality"), quality.SelectedItem?.ToString());
                PillDialogHostTests.AssertSolidAccent(win.FindControl<Button>("DownloadButton")!);
                Assert.True(PumpUntil(() => link.IsFocused), "the link box didn't take the caret");
                Assert.Contains(win.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Loc.T("YtDlp.Version", "2026.08.19") && t.IsEffectivelyVisible);

                // The same YouTube button again folds it.
                vm.YouTubeForDefaultCommand.Execute(null);
                Assert.True(PumpUntil(() => !panel.IsVisible), "the YouTube panel never folded");
                Assert.False(link.IsFocused, "the hidden link box kept the caret (Enter would still download)");

                // Esc closes, animated, once.
                win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                Assert.True(host.IsClosing);
                Assert.True(PumpUntil(() => closed > 0, 2000), "window never closed");
                PumpUntil(() => false, 250);
                Assert.Equal(1, closed);
            }
            finally { if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); } }
        });
    }

    /// <summary>Nothing has a clip of its own: a short prompt with what to do instead of a
    /// blank card; a search with no hits says so. Without the yt-dlp service there is no
    /// YouTube pill.</summary>
    [AvaloniaFact]
    public void NothingHasItsOwnVideo_ShowsThePrompt_AndASearchWithNoHitsSaysSo()
    {
        EnsureAppStyles();
        using var f = new Fixture();
        var vm = NewVm(f);
        var win = new LyricsBackgroundPickerDialog { DataContext = vm, Width = 1000, Height = 760 };
        win.Show();
        try
        {
            var prompt = win.FindControl<StackPanel>("EmptyPrompt")!;
            var none = win.FindControl<StackPanel>("NoResults")!;
            Assert.True(PumpUntil(() => prompt.IsEffectivelyVisible));
            Assert.False(none.IsEffectivelyVisible);
            Assert.DoesNotContain(win.GetVisualDescendants().OfType<Button>(), b =>
                ReferenceEquals(b.Command, vm.YouTubeForDefaultCommand) && b.IsEffectivelyVisible);

            vm.SearchText = "zzzz nothing like this";
            Assert.True(PumpUntil(() => none.IsEffectivelyVisible, 2000));
            Assert.False(prompt.IsEffectivelyVisible);
        }
        finally { if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); } }
    }

    // ── View model states ──

    /// <summary>The overrides list: albums first, then songs, each by title; an album row
    /// says it is one.</summary>
    [AvaloniaFact]
    public async Task OverridesList_AlbumsFirst_ThenSongs_ByTitle()
    {
        using var f = new Fixture();
        await f.Settings.SetLyricsBackgroundOverrideAsync(LyricsBackgroundOverrides.KeyForTrack(f.SongA), f.Clip("a.mp4"));
        await f.Settings.SetLyricsBackgroundOverrideAsync(LyricsBackgroundOverrides.KeyForTrack(f.SongB), f.Clip("b.mp4"));
        await f.Settings.SetLyricsBackgroundOverrideAsync(LyricsBackgroundOverrides.KeyForAlbum(f.Album), f.Clip("album.mp4"));
        var vm = NewVm(f);

        Assert.Equal(new[] { "Soy Peor - Single", "Diles", "Soy Peor" }, vm.Results.Select(r => r.Title));
        Assert.Equal(Loc.T("LyricsVideo.AlbumBy", "Bad Bunny"), vm.Results[0].Subtitle);
        Assert.True(vm.ShowOverridesHeader);
    }

    /// <summary>The default well says what is set (the stored copy is always background.ext,
    /// so its kind and size, not its name) and its button reads Change once there is one.</summary>
    [AvaloniaFact]
    public async Task DefaultVideo_ShowsItsKindAndSize_AndChangeOnceSet()
    {
        using var f = new Fixture();
        var clip = f.Clip("sunset.mp4", new byte[1024 * 1024 * 3 / 2]);
        var vm = NewVm(f, pick: () => Task.FromResult<string?>(clip));
        Assert.False(vm.HasDefaultVideo);
        Assert.Equal(Loc.T("LyricsBackground.ChooseVideo"), vm.DefaultChooseLabel);

        await vm.ChooseDefaultCommand.ExecuteAsync(null);
        Assert.True(vm.HasDefaultVideo);
        Assert.Equal("MP4 · " + 1.5.ToString("0.0", CultureInfo.CurrentCulture) + " MB", vm.DefaultVideoInfo);
        Assert.Equal(Loc.T("LyricsVideo.Change"), vm.DefaultChooseLabel);

        await vm.ClearDefaultCommand.ExecuteAsync(null);
        Assert.False(vm.HasDefaultVideo);
        Assert.Equal(string.Empty, vm.DefaultVideoInfo);
        Assert.False(f.Settings.HasLyricsBackgroundMedia);
    }

    /// <summary>A yt-dlp Noctis may not replace (PATH, a custom path) that is out of date: the
    /// panel offers Update, as the YouTube downloader does — not while a download runs.</summary>
    [AvaloniaFact]
    public async Task OutdatedYtDlp_NoctisMayNotReplace_OffersUpdate_ButNotMidDownload()
    {
        using var f = new Fixture();
        using var yt = new FakeYtDlp { Block = true };
        yt.Tool.LatestVersionFetcher = _ => Task.FromResult<string?>("2026.09.30");
        var vm = NewVm(f, yt);

        vm.YouTubeForDefaultCommand.Execute(null);
        Assert.True(await WaitUntilAsync(() => vm.ShowToolUpdate), "no Update offered for an outdated copy");
        Assert.Contains(Loc.T("YtDlp.UpdateAvailable"), vm.ToolVersionText);

        vm.YouTubeUrl = Link;
        var run = vm.DownloadFromYouTubeCommand.ExecuteAsync(null);
        await yt.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(vm.ShowToolUpdate);
        Assert.True(vm.CanCancelDownload);
        yt.Gate.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.ShowToolUpdate);
        Assert.True(f.Settings.HasLyricsBackgroundMedia);
    }

    /// <summary>No yt-dlp anywhere: the panel is the install state and Download never runs.</summary>
    [AvaloniaFact]
    public async Task MissingYtDlp_DownloadDoesNotRun_AndSaysInstallFirst()
    {
        using var f = new Fixture();
        using var yt = new FakeYtDlp();
        File.Delete(yt.ExePath);
        var path = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", string.Empty); // a yt-dlp on this PC's PATH would count
        try
        {
            var vm = NewVm(f, yt);
            Assert.False(vm.ToolInstalled);
            vm.YouTubeForDefaultCommand.Execute(null);
            vm.YouTubeUrl = Link;
            await vm.DownloadFromYouTubeCommand.ExecuteAsync(null);

            Assert.Equal(0, yt.Downloads);
            Assert.Equal(Loc.T("LyricsVideo.InstallFirst"), vm.YouTubeStatus);
            Assert.False(vm.HasToolVersion);
            Assert.False(vm.ShowToolUpdate);
        }
        finally { Environment.SetEnvironmentVariable("PATH", path); }
    }

    [Fact]
    public void DescribeClip_KindAndSize()
    {
        Assert.Equal(string.Empty, LyricsBackgroundPickerViewModel.DescribeClip(null));
        var dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "clip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var gif = Path.Combine(dir, "background.gif");
            File.WriteAllBytes(gif, new byte[300 * 1024]);
            Assert.Equal("GIF · 300 KB", LyricsBackgroundPickerViewModel.DescribeClip(gif));
            Assert.Equal("MP4", LyricsBackgroundPickerViewModel.DescribeClip(Path.Combine(dir, "gone.mp4")));
        }
        finally { Directory.Delete(dir, true); }
    }
}
