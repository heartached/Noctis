using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>Crossfade preset chips (3s / 6s / 10s) beside the duration slider.</summary>
public class CrossfadePresetTests
{
    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static SettingsViewModel Make()
        => new(new TestPersistenceService(), new FakeLibraryService(), new NoOpPlayHistoryService());

    [Fact]
    public void Presets_SetTheDuration_AndOnlyTheMatchingChipIsSelected()
    {
        var vm = Make();
        vm.SetCrossfadePresetCommand.Execute("3");
        Assert.Equal(3, vm.CrossfadeDuration);
        Assert.True(vm.IsCrossfade3s);
        Assert.False(vm.IsCrossfade6s);
        Assert.False(vm.IsCrossfade10s);

        vm.SetCrossfadePresetCommand.Execute("10");
        Assert.Equal(10, vm.CrossfadeDuration);
        Assert.True(vm.IsCrossfade10s);
    }

    [Fact]
    public void SliderValueBetweenPresets_SelectsNoChip()
    {
        var vm = Make();
        vm.CrossfadeDuration = 8;
        Assert.False(vm.IsCrossfade3s || vm.IsCrossfade6s || vm.IsCrossfade10s);
    }

    [Fact]
    public void Preset_ClampsToTheSliderRange_AndIgnoresGarbage()
    {
        var vm = Make();
        vm.SetCrossfadePresetCommand.Execute("40");
        Assert.Equal(12, vm.CrossfadeDuration);
        vm.SetCrossfadePresetCommand.Execute("nope");
        Assert.Equal(12, vm.CrossfadeDuration);
    }
}
