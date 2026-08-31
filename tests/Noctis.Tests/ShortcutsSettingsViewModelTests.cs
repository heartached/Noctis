using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Views;
using Avalonia.Input;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

public class ShortcutsSettingsViewModelTests
{
    private static (ShortcutsSettingsViewModel Vm, ShortcutService Service) Build(bool developerMode = false)
    {
        var service = new ShortcutService(isMac: false);
        var vm = new ShortcutsSettingsViewModel(service, () => developerMode, isMac: false);
        return (vm, service);
    }

    private static ShortcutRowViewModel Row(ShortcutsSettingsViewModel vm, ShortcutAction a) => vm.Rows.Single(r => r.Action == a);

    [Fact]
    public void Rows_CoverEveryAction_AndDeveloperRowHidesUntilDeveloperMode()
    {
        var (vm, _) = Build();
        Assert.Equal(ShortcutDefaults.All.Count, vm.Rows.Count);
        Assert.False(Row(vm, ShortcutAction.DebugPanel).IsVisible);
        Assert.DoesNotContain(vm.VisibleGroups, g => g.Name == ShortcutDefaults.GroupDeveloper);

        var (dev, _) = Build(developerMode: true);
        Assert.True(Row(dev, ShortcutAction.DebugPanel).IsVisible);
        Assert.Contains(dev.VisibleGroups, g => g.Name == ShortcutDefaults.GroupDeveloper);
    }

    [Fact]
    public void GestureParts_RenderKeyCaps()
    {
        var (vm, _) = Build();
        Assert.Equal(new[] { "Ctrl", "→" }, Row(vm, ShortcutAction.NextTrack).GestureParts);
        Assert.Equal(new[] { "Space" }, Row(vm, ShortcutAction.PlayPause).GestureParts);
        Assert.Equal(new[] { "Ctrl", "Shift", "D" }, Row(vm, ShortcutAction.DebugPanel).GestureParts);

        var mac = new ShortcutsSettingsViewModel(new ShortcutService(isMac: true), () => false, isMac: true);
        Assert.Equal(new[] { "⌘", "→" }, Row(mac, ShortcutAction.NextTrack).GestureParts);
    }

    [Fact]
    public void BeginRecord_OnlyOneRowRecordsAtATime()
    {
        var (vm, _) = Build();
        var a = Row(vm, ShortcutAction.PlayPause);
        var b = Row(vm, ShortcutAction.NextTrack);

        a.BeginRecordCommand.Execute(null);
        Assert.True(a.IsRecording);
        Assert.True(vm.IsRecording);

        b.BeginRecordCommand.Execute(null);
        Assert.False(a.IsRecording);
        Assert.True(b.IsRecording);
    }

    [Fact]
    public void TryAssign_SetsGesture_AndStopsRecording()
    {
        var (vm, service) = Build();
        var row = Row(vm, ShortcutAction.PlayPause);
        row.BeginRecordCommand.Execute(null);

        Assert.True(row.TryAssign(Key.P, KeyModifiers.None));

        Assert.Equal(new KeyGesture(Key.P), service.Get(ShortcutAction.PlayPause));
        Assert.False(row.IsRecording);
        Assert.False(row.IsDefault);
        Assert.Equal(new[] { "P" }, row.GestureParts);
    }

    [Fact]
    public void TryAssign_Conflict_ShowsMessage_KeepsRecording_LeavesGestureAlone()
    {
        var (vm, service) = Build();
        var row = Row(vm, ShortcutAction.NextTrack);
        row.BeginRecordCommand.Execute(null);

        Assert.True(row.TryAssign(Key.Space, KeyModifiers.None));

        Assert.Equal("Already used by Play / Pause", row.ConflictMessage);
        Assert.True(row.HasConflict);
        Assert.True(row.IsRecording);
        Assert.True(service.IsDefault(ShortcutAction.NextTrack));
    }

    [Fact]
    public void TryAssign_BareModifier_IsSwallowed_AndKeepsRecording()
    {
        var (vm, service) = Build();
        var row = Row(vm, ShortcutAction.PlayPause);
        row.BeginRecordCommand.Execute(null);

        Assert.True(row.TryAssign(Key.LeftShift, KeyModifiers.Shift));
        Assert.True(row.IsRecording);
        Assert.True(service.IsDefault(ShortcutAction.PlayPause));
    }

    [Fact]
    public void TryAssign_Escape_Cancels_WithoutChanging()
    {
        var (vm, service) = Build();
        var row = Row(vm, ShortcutAction.PlayPause);
        row.BeginRecordCommand.Execute(null);

        Assert.True(row.TryAssign(Key.Escape, KeyModifiers.None));
        Assert.False(row.IsRecording);
        Assert.True(service.IsDefault(ShortcutAction.PlayPause));
    }

    [Fact]
    public void TryAssign_Backspace_Unbinds()
    {
        var (vm, service) = Build();
        var row = Row(vm, ShortcutAction.PlayPause);
        row.BeginRecordCommand.Execute(null);

        Assert.True(row.TryAssign(Key.Back, KeyModifiers.None));

        Assert.Null(service.Get(ShortcutAction.PlayPause));
        Assert.True(row.IsUnbound);
        Assert.Equal(new[] { "Not set" }, row.GestureParts);
        Assert.False(row.IsRecording);
    }

    [Fact]
    public void TryAssign_WhenNotRecording_IsIgnored()
    {
        var (vm, service) = Build();
        var row = Row(vm, ShortcutAction.PlayPause);
        Assert.False(row.TryAssign(Key.P, KeyModifiers.None));
        Assert.True(service.IsDefault(ShortcutAction.PlayPause));
    }

    [Fact]
    public void Reset_AndResetAll_RestoreDefaults_AndRowsFollowTheService()
    {
        var (vm, service) = Build();
        var play = Row(vm, ShortcutAction.PlayPause);
        var next = Row(vm, ShortcutAction.NextTrack);
        service.Set(ShortcutAction.PlayPause, new KeyGesture(Key.P));
        service.Set(ShortcutAction.NextTrack, new KeyGesture(Key.N, KeyModifiers.Alt));
        Assert.False(play.IsDefault);
        Assert.Equal(new[] { "Alt", "N" }, next.GestureParts);

        play.ResetCommand.Execute(null);
        Assert.True(play.IsDefault);
        Assert.Equal(new[] { "Space" }, play.GestureParts);
        Assert.False(next.IsDefault);

        vm.ResetAllCommand.Execute(null);
        Assert.True(next.IsDefault);
        Assert.Equal(new[] { "Ctrl", "→" }, next.GestureParts);
    }

    /// <summary>The tab actually renders: one chip per visible row, none for the
    /// developer-only row until Developer Mode is on.</summary>
    [AvaloniaFact]
    public async Task ShortcutsTab_RendersOneChipPerVisibleRow()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        try
        {
            var vm = new SettingsViewModel(new PersistenceService(root), new FakeLibraryService(), new NoOpPlayHistory());
            await vm.LoadAsync();
            vm.SelectedSettingsTab = SettingsViewModel.TabShortcuts;

            var view = new SettingsView { DataContext = vm };
            var window = new Window { Width = 900, Height = 1400, Content = view };
            window.Show();

            var expected = ShortcutDefaults.All.Count(d => !d.DeveloperOnly);
            var chips = view.GetVisualDescendants().OfType<ShortcutKeyChip>().Where(c => c.IsEffectivelyVisible).ToList();
            Assert.Equal(expected, chips.Count);

            vm.DeveloperMode = true;
            window.UpdateLayout();
            chips = view.GetVisualDescendants().OfType<ShortcutKeyChip>().Where(c => c.IsEffectivelyVisible).ToList();
            Assert.Equal(ShortcutDefaults.All.Count, chips.Count);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public System.Collections.Generic.IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }
}
