using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Settings › Audio › Song Transitions greys out while Exclusive Mode is on ("Unavailable
/// while Exclusive Mode is on"), but its switch kept its value and still set the player's
/// transition mode: early AutoMix / crossfade advances ran on a stream that cannot fade, and
/// the gapless pre-roll (only taken with transitions off) never ran. The player now runs
/// transitions only while the card is usable. Also the command palette's Toggle crossfade,
/// which flipped a mirror property nothing read.
/// </summary>
public class SongTransitionsExclusiveTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private async Task<(SettingsViewModel Vm, PlayerViewModel Player)> CreateAsync()
    {
        var vm = new SettingsViewModel(new PersistenceService(_root), new FakeLibraryService(), new NoOpPlayHistoryService());
        await vm.LoadAsync();
        var audio = new FakeAudioPlayer();
        vm.SetAudioPlayer(audio);
        var player = new PlayerViewModel(audio, new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        vm.SetPlayer(player);
        return (vm, player);
    }

    [AvaloniaFact]
    public async Task ExclusiveMode_TurnsTheTransitionsOff_AndOffRestoresThem()
    {
        var (vm, player) = await CreateAsync();
        vm.IsAutoMixStyle = true;
        vm.SongTransitionsEnabled = true;
        Assert.Equal(AutoMixTransitionMode.AutoMix, player.AutoMixTransitionMode);

        vm.ExclusiveAudioEnabled = true;
        Assert.Equal(AutoMixTransitionMode.Off, player.AutoMixTransitionMode);
        Assert.True(vm.SongTransitionsEnabled); // the saved choice is kept

        // A change made elsewhere (command palette, style) while exclusive stays off too.
        vm.IsCrossfadeStyle = true;
        Assert.Equal(AutoMixTransitionMode.Off, player.AutoMixTransitionMode);

        Assert.False(vm.CrossfadeEnabled); // what the signal path shows

        vm.ExclusiveAudioEnabled = false;
        Assert.Equal(AutoMixTransitionMode.Crossfade, player.AutoMixTransitionMode);
        Assert.True(vm.CrossfadeEnabled);
    }

    [AvaloniaFact]
    public async Task PaletteToggleCrossfade_SwitchesCrossfadeOnAndOff()
    {
        var (vm, player) = await CreateAsync();
        vm.IsAutoMixStyle = true;
        Assert.False(vm.SongTransitionsEnabled);

        vm.ToggleCrossfade();
        Assert.True(vm.SongTransitionsEnabled);
        Assert.True(vm.IsCrossfadeStyle);
        Assert.Equal(AutoMixTransitionMode.Crossfade, player.AutoMixTransitionMode);

        vm.ToggleCrossfade();
        Assert.False(vm.SongTransitionsEnabled);
        Assert.Equal(AutoMixTransitionMode.Off, player.AutoMixTransitionMode);

        // AutoMix playing: the command switches to Crossfade rather than off.
        vm.IsAutoMixStyle = true;
        vm.SongTransitionsEnabled = true;
        vm.ToggleCrossfade();
        Assert.True(vm.SongTransitionsEnabled);
        Assert.True(vm.IsCrossfadeStyle);
    }
}
