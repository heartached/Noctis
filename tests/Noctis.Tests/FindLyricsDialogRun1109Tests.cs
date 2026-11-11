using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services.Lyrics;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #214: a Find Lyrics run hidden while it searched could not be brought back; Find
/// Lyrics started a new, empty dialog. GitHub #217: the dialog's options ("upgrade songs", …)
/// were forgotten after every use.
/// </summary>
public class FindLyricsDialogRun1109Tests
{
    private static Track[] Tracks() => new[] { new Track { Title = "a" }, new Track { Title = "b" } };

    /// <summary>A run that ends when the test says so.</summary>
    private sealed class GatedService : ILyricsBulkService
    {
        public readonly TaskCompletionSource<LyricsBulkSummary> Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LyricsBulkOptions? Options;

        public Task<LyricsBulkSummary> FetchAsync(IReadOnlyList<Track> tracks, IProgress<LyricsBulkProgress>? progress, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<LyricsBulkSummary> FetchAsync(IReadOnlyList<Track> tracks, LyricsBulkOptions options, IProgress<LyricsBulkProgress>? progress, CancellationToken ct)
        {
            Options = options;
            return Finish.Task;
        }

        public Task<int> RemoveAsync(IReadOnlyList<Track> tracks, IProgress<LyricsBulkProgress>? progress, CancellationToken ct)
            => Task.FromResult(0);
    }

    // ── #214 ──

    [AvaloniaFact]
    public async Task HiddenRun_IsBroughtBack_WhileRunning_AndUntilItsResultsAreClosed()
    {
        var service = new GatedService();
        var vm = new BulkLyricsViewModel(Tracks(), service, remove: false);
        Assert.False(vm.NeedsReopen); // never started: Find Lyrics opens a new dialog

        var run = vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.IsRunning);
        vm.CancelCommand.Execute(null); // "Hide"
        vm.OnDialogClosed();
        Assert.True(vm.NeedsReopen);
        Assert.Same(vm, MetadataHelper.ReusableSelectionRun(vm, remove: false));

        service.Finish.SetResult(new LyricsBulkSummary(1, 0, 0, 1, 0));
        await run;
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsDone);
        // Finished while hidden: the results have not been seen yet.
        Assert.True(vm.NeedsReopen);

        // Brought back, then closed: done with it, the next Find Lyrics starts afresh.
        vm.OnDialogClosed();
        Assert.False(vm.NeedsReopen);
        Assert.Null(MetadataHelper.ReusableSelectionRun(vm, remove: false));
    }

    [AvaloniaFact]
    public async Task ReviewRun_WaitingForSaveSelected_IsBroughtBack()
    {
        var service = new GatedService();
        var tracks = Tracks();
        var vm = new BulkLyricsViewModel(tracks, service, remove: false) { ReviewFirst = true };
        var run = vm.StartCommand.ExecuteAsync(null);
        vm.OnDialogClosed(); // hidden while it looks

        service.Finish.SetResult(new LyricsBulkSummary(0, 0, 0, 0, 0)
        {
            Finds = new[] { new LyricsBulkFind(tracks[0], new LrcLibResult { SyncedLyrics = "[00:01.00]x" }, "LRCLIB", 2, 0) },
        });
        await run;
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsReviewing);
        Assert.True(vm.NeedsReopen);
    }

    [AvaloniaFact]
    public void RemoveDialog_IsNeverReused()
    {
        var vm = new BulkLyricsViewModel(Tracks(), new GatedService(), remove: false);
        _ = vm.StartCommand.ExecuteAsync(null);

        Assert.Null(MetadataHelper.ReusableSelectionRun(vm, remove: true));
        Assert.Null(MetadataHelper.ReusableSelectionRun(null, remove: false));
    }

    // ── #217 ──

    [AvaloniaFact]
    public async Task Options_StartAsLastTicked_AndReachTheRun()
    {
        var service = new GatedService();
        var saved = new List<BulkLyricsOptionPrefs>();
        var vm = new BulkLyricsViewModel(Tracks(), service, remove: false,
            prefs: new BulkLyricsOptionPrefs(Upgrade: true, Recheck: false, ReviewFirst: true), savePrefs: saved.Add);

        Assert.True(vm.UpgradeExisting);
        Assert.False(vm.Recheck);
        Assert.True(vm.ReviewFirst);
        Assert.Empty(saved); // restoring is not a change

        var run = vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(new LyricsBulkOptions(Upgrade: true, Recheck: false, ReviewOnly: true, RetryBusy: true), service.Options);
        service.Finish.SetResult(new LyricsBulkSummary(0, 0, 0, 0, 0));
        await run;
    }

    [AvaloniaFact]
    public void Options_AreSavedWhenTicked()
    {
        var saved = new List<BulkLyricsOptionPrefs>();
        var vm = new BulkLyricsViewModel(Tracks(), new GatedService(), remove: false,
            prefs: new BulkLyricsOptionPrefs(false, false, false), savePrefs: saved.Add);

        vm.UpgradeExisting = true;
        vm.Recheck = true;

        Assert.Equal(new[]
        {
            new BulkLyricsOptionPrefs(true, false, false),
            new BulkLyricsOptionPrefs(true, true, false),
        }, saved);
    }

    [AvaloniaFact]
    public void RemoveDialog_NeitherReadsNorWritesTheFetchOptions()
    {
        var saved = new List<BulkLyricsOptionPrefs>();
        var vm = new BulkLyricsViewModel(Tracks(), new GatedService(), remove: true,
            prefs: new BulkLyricsOptionPrefs(true, true, true), savePrefs: saved.Add);

        Assert.False(vm.UpgradeExisting);
        vm.UpgradeExisting = true;
        Assert.Empty(saved);
    }

    [Fact]
    public void Options_RoundTripThroughTheSettingsFile()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new AppSettings { BulkLyricsUpgrade = true, BulkLyricsReviewFirst = true });
        var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.True(back.BulkLyricsUpgrade);
        Assert.False(back.BulkLyricsRecheck);
        Assert.True(back.BulkLyricsReviewFirst);
    }
}
