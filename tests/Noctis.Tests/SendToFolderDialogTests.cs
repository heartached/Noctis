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
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-08: Send to Folder as the rounded pill pop-up (blurred app behind, the shared
/// open/close animation, filled pill field, tidy options, a card as tall as its content),
/// plus the view-model bugs found on the way. "Bug:" cases failed on the pre-revamp code
/// (cfb840e7). Temp folders only; the copier is faked where a run must be held open.
/// </summary>
public sealed class SendToFolderDialogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", "stfd-" + Guid.NewGuid().ToString("N"));
    private readonly string _dst;

    public SendToFolderDialogTests()
    {
        _dst = Path.Combine(_root, "USB");
        Directory.CreateDirectory(_dst);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    // ── Fakes ──

    /// <summary>Plans one Copy per track; the run reports like the real service and, with
    /// <see cref="Block"/>, holds on the first song until cancelled.</summary>
    private sealed class FakeService : ISendToFolderService
    {
        public bool Block;
        public string? FailWith;
        public int Runs;
        public CancellationToken LastToken;
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<SendToFolderItem> Plan(IEnumerable<Track> tracks, string targetRoot, string? organizePattern, bool includeLyrics)
            => tracks.Select(t => new SendToFolderItem(t, t.FilePath, Path.Combine(targetRoot, Path.GetFileName(t.FilePath)),
                SendToFolderAction.Copy, null, null)).ToList();

        public Task<SendToFolderResult> CopyAsync(IReadOnlyList<SendToFolderItem> plan, IProgress<SendToFolderProgress>? progress, CancellationToken ct)
        {
            Runs++;
            LastToken = ct;
            return Task.Run(async () =>
            {
                int copied = 0, failed = 0;
                var errors = new List<string>();
                for (var i = 0; i < plan.Count; i++)
                {
                    var name = Path.GetFileName(plan[i].SourcePath);
                    progress?.Report(new SendToFolderProgress(i, plan.Count, name, i));
                    if (Block)
                    {
                        Started.TrySetResult();
                        try { await Task.Delay(Timeout.Infinite, ct); }
                        catch (OperationCanceledException) { return new SendToFolderResult(copied, 0, failed, errors, true); }
                    }
                    if (FailWith is not null)
                    {
                        failed++;
                        errors.Add($"{name}: {FailWith}");
                        progress?.Report(new SendToFolderProgress(i + 1, plan.Count, name, i, SendToFolderOutcome.Failed, FailWith));
                        continue;
                    }
                    copied++;
                    progress?.Report(new SendToFolderProgress(i + 1, plan.Count, name, i, SendToFolderOutcome.Copied));
                }
                progress?.Report(new SendToFolderProgress(plan.Count, plan.Count, string.Empty));
                return new SendToFolderResult(copied, 0, failed, errors, false);
            });
        }
    }

    private static Track[] Tracks(int n = 1) => Enumerable.Range(1, n)
        .Select(i => new Track
        {
            Id = Guid.NewGuid(), Title = $"Song {i}", Artist = "Bad Bunny", AlbumArtist = "Bad Bunny",
            Album = "Album", TrackNumber = i, FilePath = TestPaths.Primary("Music", $"song{i}.flac"),
        })
        .ToArray();

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

    private static (SendToFolderDialog Win, PillDialogHost Host) Open(SendToFolderViewModel vm, Action<int>? onClosed = null)
    {
        var win = new SendToFolderDialog(vm) { RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
        var closed = 0;
        win.Closed += (_, _) => onClosed?.Invoke(++closed);
        win.Show();
        var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
        Assert.True(PumpUntil(() => host.Card is { Opacity: > 0.999 } && host.BackdropLayer!.Opacity > 0.999),
            "open animation never settled");
        return (win, host);
    }

    private static void CloseIfOpen(Window win)
    {
        if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); }
    }

    // ── The dialog ──

    [AvaloniaFact]
    public void OpensInPillHost_Compact_WithPillControls_AndCopyOffUntilAFolderIsSet()
    {
        EnsureAppStyles();
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var vm = new SendToFolderViewModel(Tracks(1), new FakeService(), FileOrganizePlanner.DefaultPattern);
            var (win, host) = Open(vm);
            try
            {
                Assert.Equal(new CornerRadius(30), host.CornerRadius);
                Assert.Equal(Loc.T("SendToFolder.TitleOne"), vm.TitleText);
                var all = win.GetVisualDescendants().ToList();

                // The old chrome is gone: no outlined "pill" box, no accent-btn.
                Assert.DoesNotContain(all.OfType<TextBox>(), t => t.Classes.Contains("pill"));
                Assert.DoesNotContain(all.OfType<Button>(), b => b.Classes.Contains("accent-btn"));
                Assert.Empty(all.OfType<ToggleSwitch>());

                // Destination: a filled pill field (transparent rim at rest, no white outline).
                var box = win.FindControl<TextBox>("DestinationBox")!;
                Assert.Contains("pill-field", box.Classes);
                var chrome = box.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_BorderElement");
                Assert.Equal(Color.Parse("#1CFFFFFF"), AccentTestHarness.ColorOf(chrome.Background));
                Assert.Equal(new CornerRadius(999), chrome.CornerRadius);
                Assert.Equal(Colors.Transparent, AccentTestHarness.ColorOf(chrome.BorderBrush));
                Assert.Contains("pill-secondary", win.FindControl<Button>("BrowseButton")!.Classes);

                // Options: round pill checks inside one filled group (the converter's checks).
                foreach (var name in new[] { "OrganizeCheck", "LyricsCheck" })
                {
                    var check = win.FindControl<CheckBox>(name)!;
                    Assert.Contains("metadata-pill-checkbox", check.Classes);
                    Assert.Contains(check.GetVisualAncestors().OfType<Border>(), b => b.Classes.Contains("pill-well"));
                }

                // Footer: Copy solid accent and off until a folder is set; Cancel quiet.
                var copy = win.FindControl<Button>("CopyButton")!;
                PillDialogHostTests.AssertSolidAccent(copy);
                Assert.False(copy.IsEffectivelyEnabled);
                var cancel = all.OfType<Button>().Single(b => b.Command == vm.CancelCommand);
                Assert.Contains("pill-secondary", cancel.Classes);
                Assert.Equal(Loc.T("SendToFolder.Cancel"), cancel.Content);
                Assert.False(win.FindControl<Button>("ShowInFolderButton")!.IsVisible);
                Assert.Equal(Loc.T("SendToFolder.PickFolder"), vm.StatusMessage);

                // No giant empty area: before a folder is picked the card is header, field,
                // options and footer — nothing stretches it to the window.
                Assert.False(win.FindControl<ScrollViewer>("PlanList")!.IsVisible);
                Assert.True(host.Card!.Bounds.Height < 480, $"card is {host.Card.Bounds.Height}px tall with nothing to list");
                Assert.True(host.Card.Bounds.Height < win.Bounds.Height - 200);

                // A folder: the plan lands, the list comes in and Copy turns on.
                var heightBefore = host.Card.Bounds.Height;
                vm.Destination = _dst;
                Assert.True(PumpUntil(() => vm.CanStart && copy.IsEffectivelyEnabled), "Copy never turned on");
                var list = win.FindControl<ScrollViewer>("PlanList")!;
                Assert.True(list.IsVisible);
                Assert.True(PumpUntil(() => list.Opacity > 0.999), "the list never faded in");
                Assert.True(host.Card.Bounds.Height > heightBefore);
                Assert.Equal(Loc.T("SendToFolder.SummaryCopy", 1), vm.StatusMessage);
                var row = list.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("stf-row"));
                Assert.Contains(row.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Loc.T("SendToFolder.StateCopy"));
            }
            finally { CloseIfOpen(win); }
        });
    }

    /// <summary>The file pattern reads as chips of the first song's real path (folders, then
    /// the file name), not raw "{AlbumArtist}/{Album}" in monospace; they follow the toggle.</summary>
    [AvaloniaFact]
    public void ExamplePath_IsChips_FollowingTheOrganizeToggle()
    {
        EnsureAppStyles();
        var vm = new SendToFolderViewModel(Tracks(1), new FakeService(), FileOrganizePlanner.DefaultPattern);
        var (win, _) = Open(vm);
        try
        {
            Assert.Equal(new[] { "song1.flac" }, vm.ExampleChips.Select(c => c.Text));
            Assert.DoesNotContain(win.GetVisualDescendants().OfType<TextBlock>(), t => (t.Text ?? "").Contains("{AlbumArtist}"));

            win.FindControl<CheckBox>("OrganizeCheck")!.IsChecked = true;
            Assert.True(vm.OrganizeIntoFolders);
            Assert.Equal(new[] { "Bad Bunny", "Album", "01 Song 1.flac" }, vm.ExampleChips.Select(c => c.Text));
            Assert.True(vm.ExampleChips[^1].IsFile);
            PumpUntil(() => false, 30);

            var chips = win.FindControl<ItemsControl>("ExampleChips")!;
            var shown = chips.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("stf-path-chip")).ToList();
            Assert.Equal(3, shown.Count);
            Assert.Contains("file", shown[^1].Classes);
            Assert.Contains(FileOrganizePlanner.DefaultPattern, vm.ExampleTip);
        }
        finally { CloseIfOpen(win); }
    }

    [AvaloniaFact]
    public void Escape_ClosesAnimated_Once()
    {
        EnsureAppStyles();
        var closed = 0;
        var vm = new SendToFolderViewModel(Tracks(1), new FakeService(), FileOrganizePlanner.DefaultPattern);
        var (win, host) = Open(vm, n => closed = n);
        try
        {
            win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.True(host.IsClosing);
            Assert.True(win.IsVisible); // the card plays out first
            Assert.True(PumpUntil(() => closed > 0, 2000), "window never closed");
            PumpUntil(() => false, 250);
            Assert.Equal(1, closed);
        }
        finally { CloseIfOpen(win); }
    }

    /// <summary>Enter in the destination copies; an Enter while the card plays its close
    /// must not start a run (10-08 Create-twice trap).</summary>
    [AvaloniaFact]
    public async Task Enter_Copies_ButNotWhileClosing()
    {
        EnsureAppStyles();
        var service = new FakeService();
        var vm = new SendToFolderViewModel(Tracks(1), service, FileOrganizePlanner.DefaultPattern, _dst);
        await vm.PlanRebuild;
        var closed = 0;
        var (win, host) = Open(vm, n => closed = n);
        try
        {
            var box = win.FindControl<TextBox>("DestinationBox")!;
            box.Focus();
            PumpUntil(() => false, 30);
            win.Close();
            Assert.True(host.IsClosing);
            win.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            win.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Assert.True(PumpUntil(() => closed > 0, 2000));
            Assert.Equal(0, service.Runs);
        }
        finally { CloseIfOpen(win); }

        // And while open, Enter does copy (once).
        var service2 = new FakeService();
        var vm2 = new SendToFolderViewModel(Tracks(1), service2, FileOrganizePlanner.DefaultPattern, _dst);
        await vm2.PlanRebuild;
        var (win2, _) = Open(vm2);
        try
        {
            win2.FindControl<TextBox>("DestinationBox")!.Focus();
            PumpUntil(() => false, 30);
            win2.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            win2.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Assert.True(PumpUntil(() => vm2.IsDone), "Enter never copied");
            Assert.Equal(1, service2.Runs);
        }
        finally { CloseIfOpen(win2); }
    }

    /// <summary>Closing the window mid-copy (Alt+F4, the owner going away) cancels the run.</summary>
    [AvaloniaFact]
    public async Task ClosingMidCopy_CancelsTheRun()
    {
        EnsureAppStyles();
        var service = new FakeService { Block = true };
        var vm = new SendToFolderViewModel(Tracks(2), service, FileOrganizePlanner.DefaultPattern, _dst);
        await vm.PlanRebuild;
        var (win, _) = Open(vm);
        try
        {
            vm.StartCommand.Execute(null);
            Assert.True(PumpUntil(() => service.Started.Task.IsCompleted && vm.Rows[0].IsWorking), "run never started");
            win.Close();
            Assert.True(service.LastToken.IsCancellationRequested, "closing the window left the copy running");
            Assert.True(PumpUntil(() => !vm.IsCopying && !win.IsVisible));
        }
        finally { CloseIfOpen(win); }
    }

    /// <summary>Esc during a run stops it and keeps the dialog (the rows show what happened),
    /// as in the converter; the next Esc closes.</summary>
    [AvaloniaFact]
    public async Task Escape_DuringCopy_Stops_ThenCloses()
    {
        EnsureAppStyles();
        var service = new FakeService { Block = true };
        var vm = new SendToFolderViewModel(Tracks(2), service, FileOrganizePlanner.DefaultPattern, _dst);
        await vm.PlanRebuild;
        var closed = 0;
        var (win, host) = Open(vm, n => closed = n);
        try
        {
            vm.StartCommand.Execute(null);
            Assert.True(PumpUntil(() => service.Started.Task.IsCompleted && vm.Rows[0].IsWorking));
            Assert.Equal(Loc.T("SendToFolder.Stop"), vm.CancelLabel);
            Assert.Equal(Loc.T("SendToFolder.Progress", 0, 2), vm.StatusMessage);
            Assert.False(win.FindControl<TextBox>("DestinationBox")!.IsEffectivelyEnabled); // options locked
            Assert.False(win.FindControl<Button>("CopyButton")!.IsEffectivelyEnabled);      // no second run
            var bar = win.FindControl<ProgressBar>("CopyProgress")!;
            Assert.True(PumpUntil(() => bar.Opacity > 0.999), "progress bar never came in");

            win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.True(PumpUntil(() => !vm.IsCopying), "run never stopped");
            Assert.False(host.IsClosing);
            Assert.Equal(Loc.T("SendToFolder.Stopped", 0), vm.StatusMessage);
            Assert.Equal(Loc.T("SendTo.Close"), vm.CancelLabel);

            win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.True(PumpUntil(() => closed > 0, 2000));
        }
        finally { CloseIfOpen(win); }
    }

    [AvaloniaFact]
    public async Task Errors_AreListed_AndRowsMarkedFailed()
    {
        EnsureAppStyles();
        var service = new FakeService { FailWith = "Access is denied." };
        var vm = new SendToFolderViewModel(Tracks(2), service, FileOrganizePlanner.DefaultPattern, _dst);
        await vm.PlanRebuild;
        var (win, _) = Open(vm);
        try
        {
            await vm.StartCommand.ExecuteAsync(null);
            PumpUntil(() => false, 30);
            Assert.Equal(new[] { "song1.flac: Access is denied.", "song2.flac: Access is denied." }, vm.Errors);
            Assert.All(vm.Rows, r => Assert.True(r.IsFailed));
            Assert.Equal("Access is denied.", vm.Rows[0].Detail);
            Assert.Equal(Loc.T("SendToFolder.Done", 0) + " · " + Loc.T("SendToFolder.DoneFailed", 2), vm.StatusMessage);
            var panel = win.FindControl<Border>("ErrorPanel")!;
            Assert.True(panel.IsVisible);
            Assert.True(PumpUntil(() => panel.Opacity > 0.999), "errors never faded in");
            Assert.False(vm.CanShowInFolder); // nothing went over
        }
        finally { CloseIfOpen(win); }
    }

    [AvaloniaFact]
    public async Task AfterACopy_ShowInFolder_SelectsTheFile_AndCopyIsDone()
    {
        EnsureAppStyles();
        var service = new FakeService();
        var vm = new SendToFolderViewModel(Tracks(2), service, FileOrganizePlanner.DefaultPattern, _dst);
        var revealed = new List<(string, bool)>();
        vm.Reveal = (p, isFile) => revealed.Add((p, isFile));
        await vm.PlanRebuild;
        var (win, _) = Open(vm);
        try
        {
            await vm.StartCommand.ExecuteAsync(null);
            PumpUntil(() => false, 30);
            Assert.Equal(Loc.T("SendToFolder.Done", 2), vm.StatusMessage);
            Assert.All(vm.Rows, r => Assert.True(r.IsCopied));
            Assert.False(win.FindControl<Button>("CopyButton")!.IsEffectivelyEnabled); // done: no re-run
            var show = win.FindControl<Button>("ShowInFolderButton")!;
            Assert.True(show.IsVisible);
            Assert.Contains("pill-secondary", show.Classes);
            show.Command!.Execute(null);
            Assert.Equal(new[] { (Path.Combine(_dst, "song1.flac"), true) }, revealed);
        }
        finally { CloseIfOpen(win); }
    }

    // ── View model bugs ──

    /// <summary>Bug: the footer read "Finishing…" after a finished copy — Progress&lt;T&gt; plus a
    /// second Dispatcher.Post delivered the last report after the result was written.</summary>
    [AvaloniaFact]
    public async Task FinishedCopy_StatusIsTheResult_NotALateProgressReport()
    {
        var vm = new SendToFolderViewModel(Tracks(1), new FakeService(), FileOrganizePlanner.DefaultPattern, _dst);
        await vm.PlanRebuild;
        await vm.StartCommand.ExecuteAsync(null);
        for (var i = 0; i < 5; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.Equal(Loc.T("SendToFolder.Done", 1), vm.StatusMessage);
        Assert.True(vm.Rows[0].IsCopied);
    }

    /// <summary>Bug: a relative destination ("." or "Music") passed as a folder: it resolved
    /// against the app's working directory, so the songs went into the program folder.</summary>
    [AvaloniaFact]
    public async Task RelativeDestination_IsRefused()
    {
        var vm = new SendToFolderViewModel(Tracks(1), new SendToFolderService(), FileOrganizePlanner.DefaultPattern);
        vm.Destination = ".";
        await vm.PlanRebuild;
        Assert.False(vm.CanStart);
        Assert.Empty(vm.Rows);
        Assert.Equal(Loc.T("SendToFolder.NeedFullPath"), vm.DestinationError);
        Assert.True(vm.HasDestinationError);
    }

    /// <summary>Bug: the same song twice (a playlist holding it twice) titled the dialog
    /// "Send 2 songs" over a single row.</summary>
    [AvaloniaFact]
    public async Task SameSongTwice_TitleCountsItOnce()
    {
        var t = Tracks(1)[0];
        var vm = new SendToFolderViewModel(new[] { t, t }, new FakeService(), FileOrganizePlanner.DefaultPattern, _dst);
        await vm.PlanRebuild;
        Assert.Equal(Loc.T("SendToFolder.TitleOne"), vm.TitleText);
        Assert.Equal(Loc.T("SendToFolder.TitleMany", 3),
            new SendToFolderViewModel(Tracks(3), new FakeService(), FileOrganizePlanner.DefaultPattern).TitleText);
    }

    /// <summary>Bug: after Stop, Copy ran the stale plan again and every song that had already
    /// gone over failed with "The file … already exists". Now a stopped run re-plans (those
    /// read "Already there") and Copy carries on with the rest. Real copier, temp folders.</summary>
    [AvaloniaFact]
    public async Task CopyAgainAfterStop_CarriesOn_WithoutFailures()
    {
        var src = Path.Combine(_root, "Library");
        Directory.CreateDirectory(src);
        File.WriteAllBytes(Path.Combine(src, "a.flac"), new byte[10]);
        File.WriteAllBytes(Path.Combine(src, "b.flac"), new byte[20]);
        var tracks = new[] { "a.flac", "b.flac" }
            .Select(n => new Track { Id = Guid.NewGuid(), FilePath = Path.Combine(src, n), Title = n }).ToArray();
        var service = new StopAfterFirst();
        var vm = new SendToFolderViewModel(tracks, service, FileOrganizePlanner.DefaultPattern, _dst);
        await vm.PlanRebuild;

        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(service.Last!.Cancelled);
        Assert.Equal(Loc.T("SendToFolder.Stopped", 1), vm.StatusMessage);
        await vm.PlanRebuild;
        Assert.Equal(Loc.T("SendToFolder.Stopped", 1), vm.StatusMessage); // the re-plan keeps the run's status
        Assert.Equal(Loc.T("SendToFolder.StateThere"), vm.Rows[0].ChipText);

        service.StopAfterFirstFile = false;
        Assert.True(vm.CanStart, "Copy unavailable after Stop");
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(0, service.Last.Failed);
        Assert.Equal(1, service.Last.Copied);
        Assert.Equal(1, service.Last.Skipped);
        Assert.True(File.Exists(Path.Combine(_dst, "b.flac")));
    }

    /// <summary>The real copier; the first run copies the first song for real and comes back
    /// stopped, as a Stop after that song does.</summary>
    private sealed class StopAfterFirst : ISendToFolderService
    {
        private readonly SendToFolderService _real = new();
        public bool StopAfterFirstFile = true;
        public SendToFolderResult? Last;

        public IReadOnlyList<SendToFolderItem> Plan(IEnumerable<Track> tracks, string targetRoot, string? organizePattern, bool includeLyrics)
            => _real.Plan(tracks, targetRoot, organizePattern, includeLyrics);

        public async Task<SendToFolderResult> CopyAsync(IReadOnlyList<SendToFolderItem> plan, IProgress<SendToFolderProgress>? progress, CancellationToken ct)
        {
            if (StopAfterFirstFile)
            {
                var first = await _real.CopyAsync(plan.Take(1).ToList(), null, ct);
                return Last = first with { Cancelled = true };
            }
            return Last = await _real.CopyAsync(plan, progress, ct);
        }
    }

    /// <summary>All already there but lyrics missing: Copy stays available (it used to say
    /// "Everything is already in that folder" and offer nothing).</summary>
    [AvaloniaFact]
    public async Task AlreadyThereButLyricsMissing_CopyIsOffered()
    {
        var src = Path.Combine(_root, "Library");
        Directory.CreateDirectory(src);
        var song = Path.Combine(src, "a.flac");
        File.WriteAllBytes(song, new byte[10]);
        File.WriteAllText(Path.Combine(src, "a.lrc"), "[00:01.00]hi");
        File.Copy(song, Path.Combine(_dst, "a.flac"));
        var vm = new SendToFolderViewModel(new[] { new Track { Id = Guid.NewGuid(), FilePath = song, Title = "a" } },
            new SendToFolderService(), FileOrganizePlanner.DefaultPattern, _dst);
        await vm.PlanRebuild;
        Assert.True(vm.CanStart);
        Assert.Equal(Loc.T("SendToFolder.StateAddLyrics"), vm.Rows[0].ChipText);

        vm.IncludeLyrics = false;
        await vm.PlanRebuild;
        Assert.False(vm.CanStart);
        Assert.Equal(Loc.T("SendToFolder.AllThere"), vm.PlanSummary);
    }
}
