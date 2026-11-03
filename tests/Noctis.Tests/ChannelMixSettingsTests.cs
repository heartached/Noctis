using Avalonia.Headless.XUnit;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Settings for Mono Audio, Balance and the sleep timer's Fade Out: defaults, save and
/// reload through the real persistence, the centre snap, the player push, and the
/// Exclusive Mode / no-sample-stage notes.
/// </summary>
public class ChannelMixSettingsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    private SettingsViewModel CreateViewModel() => new(
        new PersistenceService(_root), new FakeLibraryService(), new NoOpPlayHistoryService());

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

    [AvaloniaFact]
    public async Task Defaults_ThenSurviveSaveAndReload()
    {
        var vm = CreateViewModel();
        await vm.LoadAsync();
        Assert.False(vm.MonoAudio);
        Assert.Equal(0, vm.AudioBalance);
        Assert.Equal(30, vm.SleepTimerFadeSeconds);
        Assert.True(vm.IsSleepFade30);

        vm.MonoAudio = true;
        vm.AudioBalance = -40;
        vm.IsSleepFade60 = true;
        await vm.SaveAsync();

        var reloaded = CreateViewModel();
        await reloaded.LoadAsync();
        Assert.True(reloaded.MonoAudio);
        Assert.Equal(-40, reloaded.AudioBalance);
        Assert.Equal(60, reloaded.SleepTimerFadeSeconds);
        Assert.True(reloaded.IsSleepFade60);
        Assert.False(reloaded.IsSleepFade30);
    }

    [AvaloniaFact]
    public async Task Balance_SnapsToCentre_AndLabelsTheSide()
    {
        var vm = CreateViewModel();
        await vm.LoadAsync();

        vm.AudioBalance = 4;
        Assert.Equal(0, vm.AudioBalance);
        Assert.Equal(Loc.T("Settings.BalanceCenter"), vm.AudioBalanceLabel);

        vm.AudioBalance = -4.6;
        Assert.Equal(-5, vm.AudioBalance);
        Assert.Equal(string.Format(Loc.T("Settings.BalanceLeftFormat"), 5), vm.AudioBalanceLabel);

        vm.AudioBalance = 250;
        Assert.Equal(100, vm.AudioBalance);
        Assert.Equal(string.Format(Loc.T("Settings.BalanceRightFormat"), 100), vm.AudioBalanceLabel);
    }

    [AvaloniaFact]
    public async Task Changes_ReachTheAudioPlayer_AndPlayer()
    {
        var vm = CreateViewModel();
        await vm.LoadAsync();
        var audio = new FakeAudioPlayer();
        vm.SetAudioPlayer(audio);
        var player = new PlayerViewModel(audio, new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        vm.SetPlayer(player);

        vm.MonoAudio = true;
        vm.AudioBalance = 50;
        Assert.Equal((true, 0.5), audio.ChannelMix);

        vm.IsSleepFade10 = true;
        Assert.Equal(10, player.SleepTimerFadeSeconds);
        vm.IsSleepFadeOff = true;
        Assert.Equal(0, player.SleepTimerFadeSeconds);
    }

    [AvaloniaFact]
    public async Task Note_ExclusiveMode_ThenNoSampleStage()
    {
        var vm = CreateViewModel();
        await vm.LoadAsync();
        var audio = new FakeAudioPlayer();
        vm.SetAudioPlayer(audio);
        Assert.Null(vm.ChannelMixNote);
        Assert.True(vm.IsChannelMixEnabled);

        vm.ExclusiveAudioEnabled = true;
        Assert.Equal(Loc.T("Settings.ChannelMixExclusiveNote"), vm.ChannelMixNote);
        Assert.False(vm.IsChannelMixEnabled);

        vm.ExclusiveAudioEnabled = false;
        audio.ChannelMixAvailable = false;
        Assert.Equal(Loc.T("Settings.ChannelMixUnavailableNote"), vm.ChannelMixNote);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 0)]
    [InlineData(10, 10)]
    [InlineData(25, 30)]
    [InlineData(45, 30)]
    [InlineData(500, 60)]
    [InlineData(-3, 0)]
    public void FadeSeconds_SnapToTheOffered(int stored, int expected) =>
        Assert.Equal(expected, AppSettings.SnapSleepTimerFadeSeconds(stored));

    // Fade Out follows the changed-defaults rule: 30 s for a new install, off for a settings
    // file saved before the setting existed, and a stored value always wins.

    private async Task<AppSettings> LoadFromAsync(string? settingsJson)
    {
        Directory.CreateDirectory(_root);
        if (settingsJson != null)
            await File.WriteAllTextAsync(Path.Combine(_root, "settings.json"), settingsJson);
        return await new PersistenceService(_root).LoadSettingsAsync();
    }

    [Fact]
    public async Task FadeOut_NewInstall_Is30Seconds()
    {
        Assert.Equal(30, new AppSettings().SleepTimerFadeSeconds);
        Assert.Equal(30, (await LoadFromAsync(null)).SleepTimerFadeSeconds);
    }

    [Fact]
    public async Task FadeOut_ExistingFileWithoutTheKey_IsOff()
    {
        var s = await LoadFromAsync("""{ "volume": 42, "theme": "Dark" }""");
        Assert.Equal(42, s.Volume);
        Assert.Equal(0, s.SleepTimerFadeSeconds);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(0)]
    public async Task FadeOut_StoredValue_IsKept(int seconds)
    {
        var s = await LoadFromAsync($$"""{ "sleepTimerFadeSeconds": {{seconds}} }""");
        Assert.Equal(seconds, s.SleepTimerFadeSeconds);
    }
}
