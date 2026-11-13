using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services.Lyrics;
using Noctis.Services.LyricsStudio;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #238: the Studio's speed pill sets the player's own speed in 0.05 steps from 0.5x to
/// 1.5x, taps keep stamping the song's time, and the speed found on opening comes back when the
/// Studio closes or its page is left.
/// </summary>
public class LyricsStudioSpeedTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noctis-studio-speed-" + Guid.NewGuid().ToString("N"));

    public LyricsStudioSpeedTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private (LyricsStudioViewModel vm, PlayerViewModel player, FakeAudioPlayer audio, Track track) Make()
    {
        var path = Path.Combine(_root, "song.mp3");
        File.WriteAllText(Path.ChangeExtension(path, ".lrc"), "[00:05.00]First line here\n[00:12.50]Second line here\n");
        var track = new Track { Title = "song", Artist = "A", FilePath = path, Duration = TimeSpan.FromMinutes(3) };
        var audio = new FakeAudioPlayer();
        var player = new PlayerViewModel(audio, new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new LyricsStudioViewModel(new[] { track }, new NoRunEngine(_root), new LyricsWriter(null!, null), new FakeLibraryService(), player,
            () => new AppSettings(), _ => { });
        vm.Confirm = _ => Task.FromResult(true);
        vm.Selected = vm.Queue[0];
        return (vm, player, audio, track);
    }

    [AvaloniaFact]
    public void SlowerAndFaster_StepTheEngineByFivePercent_Between50And150()
    {
        var (vm, player, audio, _) = Make();
        vm.SlowerCommand.Execute(null);
        Assert.Equal(95, player.PlaybackRatePercent);
        Assert.Equal(0.95, audio.PlaybackRate, 3);
        for (var i = 0; i < 20; i++) vm.SlowerCommand.Execute(null);
        Assert.Equal(50, player.PlaybackRatePercent);
        Assert.Equal("0.5×", player.PlaybackRateText);
        for (var i = 0; i < 30; i++) vm.FasterCommand.Execute(null);
        Assert.Equal(150, player.PlaybackRatePercent);
        Assert.Equal(1.5, audio.PlaybackRate, 3);
    }

    [AvaloniaFact]
    public void ASpeedOffTheGrid_SnapsToIt_AndOneAbove150_StepsDownTo150()
    {
        var (vm, player, _, _) = Make();
        player.SetPlaybackRateCommand.Execute("175"); // the island's menu
        vm.SlowerCommand.Execute(null);
        Assert.Equal(150, player.PlaybackRatePercent);
        player.PlaybackRatePercent = 77;
        vm.FasterCommand.Execute(null);
        Assert.Equal(80, player.PlaybackRatePercent);
    }

    [AvaloniaFact]
    public async Task Close_GivesBackTheSpeedTheStudioFound()
    {
        var (vm, player, audio, _) = Make();
        player.SetPlaybackRateCommand.Execute("75");
        vm.SlowerCommand.Execute(null);
        vm.SlowerCommand.Execute(null);
        Assert.Equal(65, player.PlaybackRatePercent);
        await vm.CloseCommand.ExecuteAsync(null);
        Assert.Equal(75, player.PlaybackRatePercent);
        Assert.Equal(0.75, audio.PlaybackRate, 3);
    }

    [AvaloniaFact]
    public async Task Close_LeavesTheSpeedAlone_WhenTheStudioNeverChangedIt()
    {
        var (vm, player, _, _) = Make();
        player.SetPlaybackRateCommand.Execute("125");
        await vm.CloseCommand.ExecuteAsync(null);
        Assert.Equal(125, player.PlaybackRatePercent);
    }

    [AvaloniaFact]
    public void LeavingTheStudioPage_GivesBackTheSpeed_AndThePillIsInTheNudgeBar()
    {
        EnsureAppResources();
        var (vm, player, _, _) = Make();
        var panel = new LyricsStudioPanel { DataContext = vm, ShowHeader = false };
        var win = new Window { Width = 1200, Height = 800, Content = panel };
        win.Show();
        try
        {
            Settle(win);
            var bar = panel.GetVisualDescendants().OfType<WrapPanel>().Single(w => w.Classes.Contains("review-nudge"));
            var pill = bar.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("studio-speed"));
            Assert.True(pill.IsEffectivelyVisible);
            var slower = pill.GetVisualDescendants().OfType<Button>().First();
            Assert.True(slower.IsEffectivelyEnabled);
            slower.Command!.Execute(slower.CommandParameter);
            Settle(win);
            Assert.Equal(95, player.PlaybackRatePercent);
            Assert.Contains(pill.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "0.95×");

            win.Content = null; // the page navigated away: no Close on the page
            Settle(win);
            Assert.Equal(100, player.PlaybackRatePercent);
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public async Task ATapAtHalfSpeed_StampsTheSongsTime_NotTheWallClock()
    {
        var (vm, player, audio, track) = Make();
        var line = vm.ReviewLines[0];
        await vm.StartTapCommand.ExecuteAsync(line); // plays the song
        Assert.Equal(track.Id, player.CurrentTrack?.Id);
        vm.SlowerCommand.Execute(null);
        for (var i = 0; i < 9; i++) vm.SlowerCommand.Execute(null);
        Assert.Equal(0.5, audio.PlaybackRate, 3);

        // The player's position is the song's (media) time; at 0.5x it advances 400 ms of song
        // in 800 ms of wall time, and the tap stamps the 400 ms.
        player.Position = TimeSpan.FromMilliseconds(5_200);
        vm.TapCommand.Execute(null);
        player.Position = TimeSpan.FromMilliseconds(5_600);
        vm.TapCommand.Execute(null);

        Assert.Equal(TimeSpan.FromMilliseconds(5_200), line.Words[0].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(5_600), line.Words[1].Start);
    }

    private static void EnsureAppResources()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("ChevronDownThinIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = Avalonia.Media.FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml")
        });
    }

    private static void Settle(Window win)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            win.UpdateLayout();
        }
    }

    private sealed class NoRunEngine : ILyricsStudioEngine
    {
        public NoRunEngine(string root) => Models = StudioTestModel.Installed(root);
        public bool HasFfmpeg => true;
        public WhisperModelManager Models { get; }
        public IDisposable OpenSession(WhisperModelSize model) => throw new InvalidOperationException("no runs here");
        public Task<LyricsStudioResult> ProcessAsync(Track track, LyricsStudioOptions options, IProgress<LyricsStudioProgress>? progress, System.Threading.CancellationToken ct) =>
            throw new InvalidOperationException("no runs here");
    }
}
