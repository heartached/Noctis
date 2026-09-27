using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// macOS diagnostics (09-26): Settings writes one "SettingsStyle" line per tab to the session
/// log with what its styles resolved to. On Windows the values are the designed ones; a Mac
/// log that differs points at the values, one that matches points at the drawing.
/// </summary>
public class SettingsStyleProbeTests
{
    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    [AvaloniaFact]
    public async Task EachTab_LogsItsResolvedStyleValuesOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        var vm = new SettingsViewModel(new PersistenceService(root), new FakeLibraryService(), new NoOpPlayHistory());
        await vm.LoadAsync();
        SettingsView.StyleProbeTabs.Clear();
        DebugLog.Clear();

        vm.SelectedSettingsTab = SettingsViewModel.TabIntegrations;
        var view = new SettingsView { DataContext = vm };
        var win = new Window { Width = 1100, Height = 820, Content = view };
        win.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            vm.SelectedSettingsTab = SettingsViewModel.TabAppearance;
            Dispatcher.UIThread.RunJobs();
            vm.SelectedSettingsTab = SettingsViewModel.TabIntegrations;   // already logged
            Dispatcher.UIThread.RunJobs();

            var lines = DebugLog.Snapshot().Split(Environment.NewLine).Where(l => l.Contains("[SettingsStyle]")).ToList();
            var integrations = Assert.Single(lines, l => l.Contains("] Integrations:"));
            Assert.Contains("title opacity=0.45", integrations);
            Assert.Contains("pill radius=999,999,999,999", integrations);
            Assert.Contains("border=999,999,999,999", integrations);
            var appearance = Assert.Single(lines, l => l.Contains("] Appearance:"));
            Assert.Contains("slider track opacity=0.45", appearance);
            Assert.Contains("radius=999", appearance);
        }
        finally
        {
            win.Close();
            SettingsView.StyleProbeTabs.Clear();
        }
    }
}
