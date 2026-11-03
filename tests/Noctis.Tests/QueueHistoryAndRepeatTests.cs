using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Localization;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord (Luwi) "last played songs in the Queue view" and GitHub #144 "repeat once / repeat
/// infinitely". The played songs come from the player's History (Keep Played Songs, GitHub
/// #124/#134), now one click away in the Queue panel; the repeat modes already loop forever,
/// pinned here through the real natural-end path (TrackEnded → AdvanceQueue).
/// </summary>
public class QueueHistoryAndRepeatTests
{
    private static (PlayerViewModel vm, FakeAudioPlayer audio) CreateVm()
    {
        var audio = new FakeAudioPlayer();
        var vm = new PlayerViewModel(audio, new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        return (vm, audio);
    }

    private static List<Track> Tracks(int n) => Enumerable.Range(1, n).Select(i => new Track
    {
        Id = Guid.NewGuid(),
        Title = $"t{i}",
        Artist = "A",
        FilePath = TestPaths.Primary("t", $"t{i}.mp3"),
        Duration = TimeSpan.FromMinutes(3),
    }).ToList();

    private static void EndNaturally(FakeAudioPlayer audio)
    {
        audio.RaiseTrackEnded();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void HistoryToggle_ShowsWhatAlreadyPlayed_NewestNextToNowPlaying()
    {
        var (vm, audio) = CreateVm();
        var t = Tracks(4);
        vm.ReplaceQueueAndPlay(t, 0);
        EndNaturally(audio);
        EndNaturally(audio); // t1, t2 played; t3 playing
        Assert.Equal(new[] { t[3] }, vm.QueueRows); // off: the rows are Up Next only

        vm.ToggleShowPlayedInQueueCommand.Execute(null);

        Assert.True(vm.KeepPlayedInQueue);
        // Played songs were recorded while the toggle was off, so they appear at once.
        Assert.Equal(new[] { t[0], t[1], t[2], t[3] }, vm.QueueRows);
        Assert.Equal(QueueRowKind.Played, vm.GetQueueRowKind(1));
        Assert.Equal(QueueRowKind.Current, vm.GetQueueRowKind(2));
        Assert.Equal(2, vm.QueueCurrentRow);
        Assert.True(vm.HasPlayedQueueRows);

        vm.ToggleShowPlayedInQueueCommand.Execute(null);
        Assert.False(vm.KeepPlayedInQueue);
        Assert.Equal(new[] { t[3] }, vm.QueueRows);
        Assert.Equal(new[] { t[1], t[0] }, vm.History); // hiding them keeps the history
    }

    [AvaloniaFact]
    public void HistoryToggle_PlayingAPlayedRow_PlaysItAgain_AndPutsTheRestBackUpNext()
    {
        var (vm, audio) = CreateVm();
        var t = Tracks(3);
        vm.ReplaceQueueAndPlay(t, 0);
        EndNaturally(audio);
        EndNaturally(audio); // t3 playing
        vm.ToggleShowPlayedInQueueCommand.Execute(null);

        vm.PlayQueueRow(0); // t1, the oldest played row

        Assert.Same(t[0], vm.CurrentTrack);
        Assert.Equal(new[] { t[1], t[2] }, vm.UpNext);
    }

    [AvaloniaFact]
    public void RepeatOne_LoopsTheSameSong_OnEveryNaturalEnd()
    {
        var (vm, audio) = CreateVm();
        var t = Tracks(2);
        vm.ReplaceQueueAndPlay(t, 0);
        vm.CycleRepeatCommand.Execute(null);
        vm.CycleRepeatCommand.Execute(null);
        Assert.Equal(RepeatMode.One, vm.RepeatMode);

        for (var i = 0; i < 5; i++)
        {
            EndNaturally(audio);
            Assert.Same(t[0], vm.CurrentTrack);
        }
        Assert.Equal(6, audio.PlayedPaths.Count(p => p == t[0].FilePath));
        Assert.Equal(new[] { t[1] }, vm.UpNext);
        // Gapless must not pre-roll the next song under Repeat One.
        vm.GaplessEnabled = true;
        Assert.False(vm.TryAdvanceForGapless(TimeSpan.FromSeconds(179), TimeSpan.FromMinutes(3)));
    }

    [AvaloniaFact]
    public void RepeatAll_LoopsTheWholeQueue_PassAfterPass()
    {
        var (vm, audio) = CreateVm();
        var t = Tracks(3);
        vm.ReplaceQueueAndPlay(t, 0);
        vm.CycleRepeatCommand.Execute(null);
        Assert.Equal(RepeatMode.All, vm.RepeatMode);

        var played = new List<Track> { vm.CurrentTrack! };
        for (var i = 0; i < 8; i++)
        {
            EndNaturally(audio);
            played.Add(vm.CurrentTrack!);
        }

        Assert.Equal(new[] { t[0], t[1], t[2], t[0], t[1], t[2], t[0], t[1], t[2] }, played);
    }

    [AvaloniaFact]
    public void RepeatOff_StopsAtTheEndOfTheQueue()
    {
        var (vm, audio) = CreateVm();
        var t = Tracks(2);
        vm.ReplaceQueueAndPlay(t, 0);
        EndNaturally(audio);
        EndNaturally(audio);
        Assert.Null(vm.CurrentTrack);
    }

    [AvaloniaFact]
    public void RepeatTip_NamesTheMode()
    {
        var (vm, _) = CreateVm();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.Equal(Loc.T("Repeat.OffTip"), vm.RepeatModeTip);
        vm.CycleRepeatCommand.Execute(null);
        Assert.Equal(Loc.T("Repeat.AllTip"), vm.RepeatModeTip);
        vm.CycleRepeatCommand.Execute(null);
        Assert.Equal(Loc.T("Repeat.OneTip"), vm.RepeatModeTip);
        Assert.Contains(nameof(PlayerViewModel.RepeatModeTip), raised);
        Assert.NotEqual("Repeat.OneTip", vm.RepeatModeTip); // the key resolves to text
    }

    [Fact]
    public void QueuePanelHeader_HasHistoryAndRepeatChips_WiredToThePlayer()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Noctis.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var xaml = File.ReadAllText(Path.Combine(root!.FullName, "src", "Noctis", "Views", "MainWindow.axaml"));

        string Chip(string name)
        {
            var at = xaml.IndexOf($"x:Name=\"{name}\"", StringComparison.Ordinal);
            Assert.True(at > 0, name);
            return xaml.Substring(at, xaml.IndexOf("</Button>", at, StringComparison.Ordinal) - at);
        }

        var history = Chip("QueueHistoryButton");
        Assert.Contains("Command=\"{Binding Player.ToggleShowPlayedInQueueCommand}\"", history);
        Assert.Contains("{loc:T Queue.ShowPlayedTip}", history);
        var repeat = Chip("QueueRepeatButton");
        Assert.Contains("Command=\"{Binding Player.CycleRepeatCommand}\"", repeat);
        Assert.Contains("ToolTip.Tip=\"{Binding Player.RepeatModeTip}\"", repeat);
        Assert.Contains("RepeatOneIcon", repeat);
    }
}
