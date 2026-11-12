using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Controls;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord (Major D ark): an optional Stop button on the island after Play/Pause (Settings →
/// Player → Player island buttons, off by default), with a sub-toggle that shows it only while
/// Exclusive Mode is on. Stop halts the engine (which lets go of the device), keeps the song and
/// queue at the song's start, and Play starts the song again from the beginning.
/// </summary>
[Collection("MetadataServiceStatics")]
public class IslandStopButtonTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NoctisTests", "stop-" + Guid.NewGuid().ToString("N"));

    public IslandStopButtonTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static Track Song(string title) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Artist = "A",
        FilePath = TestPaths.Primary("m", $"{title}.mp3"),
        SourceType = SourceType.Local,
        Duration = TimeSpan.FromMinutes(3),
    };

    private static (PlayerViewModel Player, FakeAudioPlayer Audio, Track First, Track Second) Playing()
    {
        var audio = new FakeAudioPlayer();
        var library = new FakeLibraryService();
        var player = new PlayerViewModel(audio, library, new TestPersistenceService(), new FakeAnimatedCoverService());
        var a = Song("First");
        var b = Song("Second");
        library.TrackList.AddRange(new[] { a, b });
        player.ReplaceQueueAndPlay(new[] { a, b }, 0);
        return (player, audio, a, b);
    }

    [AvaloniaFact]
    public void Stop_HaltsTheEngine_KeepsTheSongAndQueue_AtTheStart()
    {
        var (player, audio, first, second) = Playing();
        player.Position = TimeSpan.FromSeconds(95);
        player.PositionFraction = 95 / 180.0;
        Assert.Equal(PlaybackState.Playing, audio.State);

        player.StopPlaybackCommand.Execute(null);

        Assert.Equal(PlaybackState.Stopped, audio.State); // IAudioPlayer.Stop: media unloaded, output closed
        Assert.Equal(PlaybackState.Stopped, player.State);
        Assert.Same(first, player.CurrentTrack);
        Assert.Equal(new[] { second }, player.UpNext);
        Assert.Equal(TimeSpan.Zero, player.Position);
        Assert.Equal(0, player.PositionFraction);
        Assert.Equal("0:00", player.PositionText);
        Assert.Equal("3:00", player.RemainingTimeText);
    }

    [AvaloniaFact]
    public void Play_AfterStop_StartsTheSameSongFromTheBeginning()
    {
        var (player, audio, first, _) = Playing();
        player.Position = TimeSpan.FromSeconds(95);

        player.StopPlaybackCommand.Execute(null);
        player.PlayPauseCommand.Execute(null);

        Assert.Equal(PlaybackState.Playing, player.State);
        Assert.Same(first, player.CurrentTrack);
        Assert.Equal(new[] { first.FilePath, first.FilePath }, audio.PlayedPaths);
        Assert.Equal(-1, audio.PendingSeekMs);
    }

    /// <summary>An audiobook that remembers its place starts over too after Stop.</summary>
    [AvaloniaFact]
    public void Play_AfterStop_StartsOver_EvenForASongThatRemembersItsPosition()
    {
        var (player, audio, first, _) = Playing();
        first.RememberPlaybackPosition = true;
        player.Position = TimeSpan.FromSeconds(95);

        player.StopPlaybackCommand.Execute(null);
        player.PlayPauseCommand.Execute(null);

        Assert.Equal(-1, audio.PendingSeekMs);
        Assert.Equal(0, first.SavedPositionMs);
    }

    /// <summary>A seek made while stopped is kept for the next Play; a Stop after it drops it.</summary>
    [AvaloniaFact]
    public void Stop_DropsAResumePointSetWhileStopped()
    {
        var (player, audio, _, _) = Playing();
        player.StopPlaybackCommand.Execute(null);
        player.SeekToPositionCommand.Execute(0.5);
        player.StopPlaybackCommand.Execute(null);
        player.PlayPauseCommand.Execute(null);

        Assert.Equal(-1, audio.PendingSeekMs);
        Assert.Equal(TimeSpan.Zero, player.Position);
    }

    [AvaloniaFact]
    public void Stop_WithNothingLoaded_DoesNothing()
    {
        var audio = new FakeAudioPlayer();
        var player = new PlayerViewModel(audio, new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        player.StopPlaybackCommand.Execute(null);
        Assert.Null(player.CurrentTrack);
        Assert.Equal(PlaybackState.Stopped, player.State);
        Assert.Empty(audio.PlayedPaths);
    }

    [AvaloniaFact]
    public void Visibility_FollowsTheToggle_AndTheExclusiveModeSubToggle()
    {
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        Assert.False(player.IslandStopButtonVisible);

        player.IslandShowStop = true;
        Assert.True(player.IslandStopButtonVisible);

        player.IslandStopOnlyInExclusiveMode = true;
        Assert.False(player.IslandStopButtonVisible);
        player.ExclusiveModeEnabled = true;
        Assert.True(player.IslandStopButtonVisible);

        player.IslandShowStop = false;
        Assert.False(player.IslandStopButtonVisible);
    }

    /// <summary>The button sits right after Play/Pause and adds its 36px to the island like the
    /// other extras (the #211 fit logic budgets it); hidden, it hands the width back.</summary>
    [AvaloniaFact]
    public void IslandButton_SitsAfterPlayPause_AndBudgetsItsWidth()
    {
        Assert.False(new AppSettings().PlaybackBarShowStop);
        Assert.False(new AppSettings().PlaybackBarStopOnlyInExclusiveMode);
        var (player, audio, _, _) = Playing();

        var bar = new PlaybackBarView { DataContext = player, CompactWhenLyricsPageActive = false };
        var win = new Window { Width = 1000, Height = 200, Content = bar };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var island = bar.FindControl<Border>("IslandBorder")!;
            var button = bar.FindControl<Button>("StopButton")!;
            var stock = island.Width;
            Assert.False(button.IsVisible);

            player.IslandShowStop = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(button.IsEffectivelyVisible);
            Assert.Equal(stock + 36, island.Width);
            Assert.Equal("Stop", ToolTip.GetTip(button));
            Assert.Equal("Stop", AutomationProperties.GetName(button));
            Assert.Equal(TransportKind.Stop, Assert.IsType<TransportGlyph>(button.Content).Kind);

            // Right after Play/Pause in the transport group.
            var group = Assert.IsType<StackPanel>(button.Parent);
            var index = group.Children.IndexOf(button);
            Assert.Same(player.PlayPauseCommand, ((Button)group.Children[index - 1]).Command);

            button.Command!.Execute(null);
            Assert.Equal(PlaybackState.Stopped, audio.State);

            player.IslandStopOnlyInExclusiveMode = true;
            Dispatcher.UIThread.RunJobs();
            Assert.False(button.IsVisible);
            Assert.Equal(stock, island.Width);

            player.ExclusiveModeEnabled = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(button.IsVisible);
            Assert.Equal(stock + 36, island.Width);
        }
        finally { win.Close(); }
    }

    // ── Settings ──

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private SettingsViewModel CreateSettings() => new(
        new PersistenceService(Path.Combine(_root, "data")), new FakeLibraryService(), new NoOpPlayHistoryService());

    [AvaloniaFact]
    public async Task Settings_MirrorOntoThePlayer_SurviveReload_AndReset()
    {
        var vm = CreateSettings();
        await vm.LoadAsync();
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        vm.SetPlayer(player);
        Assert.False(vm.PlaybackBarShowStop);
        Assert.False(vm.PlaybackBarStopOnlyInExclusiveMode);
        Assert.False(player.IslandStopButtonVisible);

        vm.PlaybackBarShowStop = true;
        Assert.True(player.IslandShowStop);
        Assert.True(player.IslandStopButtonVisible);

        vm.PlaybackBarStopOnlyInExclusiveMode = true;
        Assert.True(player.IslandStopOnlyInExclusiveMode);
        Assert.False(player.IslandStopButtonVisible);

        // The Exclusive Mode switch in Settings → Audio drives the sub-toggle live.
        vm.ExclusiveAudioEnabled = true;
        Assert.True(player.ExclusiveModeEnabled);
        Assert.True(player.IslandStopButtonVisible);
        vm.ExclusiveAudioEnabled = false;
        Assert.False(player.IslandStopButtonVisible);
        await vm.SaveAsync();

        var reloaded = CreateSettings();
        await reloaded.LoadAsync();
        Assert.True(reloaded.PlaybackBarShowStop);
        Assert.True(reloaded.PlaybackBarStopOnlyInExclusiveMode);

        reloaded.ResetSettingsToDefaults(new AppSettings());
        Assert.False(reloaded.PlaybackBarShowStop);
        Assert.False(reloaded.PlaybackBarStopOnlyInExclusiveMode);
    }
}
