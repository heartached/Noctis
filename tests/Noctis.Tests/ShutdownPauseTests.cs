using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// PauseForShutdown: ShutdownAsync's saves run for seconds after the window is
/// gone, so playback must be silenced first — but with a pause-only call, never
/// the PlayPause toggle (which would resume an already-paused player) and never
/// Stop (which would clear the track/position the queue snapshot needs).
/// </summary>
public class ShutdownPauseTests
{
    private static (PlayerViewModel vm, FakeAudioPlayer player) CreateVm()
    {
        var player = new FakeAudioPlayer();
        var vm = new PlayerViewModel(
            player, new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        return (vm, player);
    }

    private static Track Trk(string name) => new()
    {
        Id = Guid.NewGuid(),
        Title = name,
        Artist = "A",
        FilePath = TestPaths.Primary("t", $"{name}.mp3"),
        Duration = TimeSpan.FromMinutes(3)
    };

    [AvaloniaFact]
    public void WhilePlaying_PausesEngineAndKeepsTrack()
    {
        var (vm, player) = CreateVm();
        var track = Trk("playing");
        vm.ReplaceQueueAndPlay(new[] { track }, 0);
        Assert.Equal(PlaybackState.Playing, vm.State);

        vm.PauseForShutdown();

        Assert.Equal(PlaybackState.Paused, player.State);
        Assert.Equal(PlaybackState.Paused, vm.State);
        Assert.Equal(track.Id, vm.CurrentTrack?.Id); // snapshot still has the track
    }

    [AvaloniaFact]
    public void AlreadyPaused_DoesNotResume()
    {
        var (vm, player) = CreateVm();
        vm.ReplaceQueueAndPlay(new[] { Trk("paused") }, 0);
        vm.PlayPauseCommand.Execute(null); // user paused before quitting

        vm.PauseForShutdown();

        Assert.Equal(PlaybackState.Paused, player.State);
        Assert.Equal(PlaybackState.Paused, vm.State);
    }

    [AvaloniaFact]
    public void NothingLoaded_IsANoOp()
    {
        var (vm, player) = CreateVm();

        vm.PauseForShutdown();

        Assert.Equal(PlaybackState.Stopped, player.State);
        Assert.Equal(PlaybackState.Stopped, vm.State);
    }

    /// <summary>
    /// GitHub #243: the exit pause (with the user's fade) is requested before the background-work
    /// stops (each may wait up to 3 s) and the saves; it used to sit behind them, so the song
    /// played on for seconds after the window closed.
    /// </summary>
    [Fact]
    public async Task ShutdownSequence_SilencesBeforeTheSlowWork()
    {
        var steps = new List<string>();
        var stopGate = new TaskCompletionSource();
        var saveGate = new TaskCompletionSource();

        var run = App.RunShutdownSequenceAsync(
            () => steps.Add("silence"),
            () => { steps.Add("stop"); return stopGate.Task; },
            () => { steps.Add("save"); return saveGate.Task; },
            TimeSpan.FromSeconds(10));

        // The background stops are still running: the music is already being silenced.
        Assert.Equal(new[] { "silence", "stop" }, steps);
        stopGate.SetResult();
        await Task.Yield();
        saveGate.SetResult();
        await run;
        Assert.Equal(new[] { "silence", "stop", "save" }, steps);
    }

    /// <summary>A failing pause never skips the saves; a hung save is abandoned at the deadline.</summary>
    [Fact]
    public async Task ShutdownSequence_PauseFailureStillSaves_AndASlowSaveIsBounded()
    {
        var saved = false;
        var run = App.RunShutdownSequenceAsync(
            () => throw new InvalidOperationException("engine gone"),
            () => Task.CompletedTask,
            () => { saved = true; return new TaskCompletionSource().Task; },
            TimeSpan.FromMilliseconds(50));
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(saved);
    }

    /// <summary>
    /// GitHub #243: closing the real main window (X, tray Quit) starts the exit pause in Closing,
    /// before the window's teardown and before App's shutdown handler runs at all; the player is
    /// already paused when Closed is raised. Close-to-tray (off by default) is not an exit.
    /// </summary>
    [AvaloniaFact]
    public void ClosingTheMainWindow_PausesBeforeTheTeardown()
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var player = new FakeAudioPlayer();
        var services = new ServiceCollection();
        typeof(App).Assembly.GetType("Noctis.Program")!
            .GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [services]);
        services.AddSingleton<IAudioPlayer>(player);
        services.AddSingleton<ILibraryService>(new FakeLibraryService());
        services.AddSingleton<IPersistenceService>(new TestPersistenceService());
        var provider = services.BuildServiceProvider();
        var previous = App.Services;
        App.Services = provider;
        try
        {
            var vm = provider.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow(vm) { Width = 1200, Height = 800 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            vm.Player.ReplaceQueueAndPlay(new[] { Trk("exit") }, 0);
            Assert.Equal(PlaybackState.Playing, player.State);

            PlaybackState? atClosed = null;
            window.Closed += (_, _) => atClosed = player.State;
            window.Close();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(PlaybackState.Paused, atClosed);
            Assert.Equal(PlaybackState.Paused, vm.Player.State);
        }
        finally
        {
            App.Services = previous!;
            provider.Dispose();
        }
    }
}
