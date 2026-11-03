using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// 11-03 Settings audit: the real SettingsView, opened on each of these tabs, binds without a
/// single binding warning. The owner's log showed one every time Account &amp; Devices opened
/// without an account (PrimaryAccount.Name with PrimaryAccount null).
/// </summary>
public class SettingsTabsBindingAuditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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

    private static void Pump(int frames = 6)
    {
        for (var i = 0; i < frames; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class BindingSink : ILogSink, IDisposable
    {
        private readonly ILogSink? _previous = Logger.Sink;
        public List<string> Messages { get; } = new();
        public BindingSink() => Logger.Sink = this;
        public void Dispose() => Logger.Sink = _previous;
        public bool IsEnabled(LogEventLevel level, string area) =>
            level >= LogEventLevel.Warning && area == LogArea.Binding;
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
            Messages.Add($"{source?.GetType().Name}: {messageTemplate}");
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) =>
            Messages.Add($"{source?.GetType().Name}: {AvaloniaLogBridge.Format(messageTemplate, propertyValues)}");
    }

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    [AvaloniaTheory]
    [InlineData(SettingsViewModel.TabLibrary)]
    [InlineData(SettingsViewModel.TabAdvanced)]
    [InlineData(SettingsViewModel.TabAccountDevices)]
    [InlineData(SettingsViewModel.TabIntegrations)]
    [InlineData(SettingsViewModel.TabPlugins)]
    [InlineData(SettingsViewModel.TabStatistics)]
    [InlineData(SettingsViewModel.TabAbout)]
    public async Task Tab_Opens_WithNoBindingWarnings(string tab)
    {
        EnsureAppStyles();
        var vm = new SettingsViewModel(new PersistenceService(_root), new FakeLibraryService(), new NoOpPlayHistory());
        await vm.LoadAsync();
        using var sink = new BindingSink();
        var view = new SettingsView { DataContext = vm };
        var window = new Window { Width = 1200, Height = 900, Content = view };
        window.Show();
        Pump();
        vm.SelectedSettingsTab = tab;
        Pump(12);
        window.Close();
        await vm.StopNoctisServerAsync();

        // The close button reaches MainWindowViewModel through the window; this bare test window has none.
        var messages = sink.Messages.Distinct().Where(m => !m.Contains("$parent[Window]", StringComparison.Ordinal)).ToList();
        Assert.True(messages.Count == 0, string.Join("\n", messages.Take(30)));
    }
}
