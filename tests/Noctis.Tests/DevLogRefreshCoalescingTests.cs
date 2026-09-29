using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Developer Mode keeps a live copy of the session log. Every DebugLog write used to post
/// its own UI-thread rebuild — a join of the whole 500-line ring — so a burst of playback
/// warnings (mirrored into that log in dev mode) queued one full rebuild per line. One
/// pending refresh now absorbs the burst, and the text still ends up current.
/// </summary>
public class DevLogRefreshCoalescingTests
{
    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static int Queued(SettingsViewModel vm) =>
        (int)typeof(SettingsViewModel).GetField("_devLogRefreshQueued", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;

    [AvaloniaFact]
    public void BurstOfWrites_QueuesOneRefresh_AndTheTextEndsCurrent()
    {
        var loggerOn = DebugLogger.IsEnabled;
        var mirror = DebugLogger.MirrorPlaybackToSessionLog;
        var bridge = DebugLog.VlcBridgeEnabled;
        using var persistence = new TestPersistenceService();
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistory());
        try
        {
            vm.DeveloperMode = true;
            Dispatcher.UIThread.RunJobs();

            var tag = Guid.NewGuid().ToString("N");
            for (var i = 0; i < 50; i++)
                DebugLog.Write("Test", $"{tag} burst line {i}");
            Assert.Equal(1, Queued(vm));

            Dispatcher.UIThread.RunJobs();
            Assert.Contains($"{tag} burst line 49", vm.DevLogText);

            // A later write schedules again.
            DebugLog.Write("Test", $"{tag} after");
            Assert.Equal(1, Queued(vm));
            Dispatcher.UIThread.RunJobs();
            Assert.Contains($"{tag} after", vm.DevLogText);
        }
        finally
        {
            vm.DeveloperMode = false;
            DebugLogger.IsEnabled = loggerOn;
            DebugLogger.MirrorPlaybackToSessionLog = mirror;
            DebugLog.VlcBridgeEnabled = bridge;
        }
    }
}
