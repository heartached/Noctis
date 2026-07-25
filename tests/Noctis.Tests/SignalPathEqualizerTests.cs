using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The signal-path badge used to treat the Equalizer master toggle as "DSP is active".
/// The stock install ships that toggle ON sitting on the Flat preset, which the player
/// applies as a true bypass — so every fresh install permanently read "Enhanced" and
/// could never show Lossless or Bit-perfect. The badge now follows whether the EQ
/// actually alters the signal, the way ReplayGain already did.
/// </summary>
public class SignalPathEqualizerTests
{
    private static (PlayerViewModel Player, FakeAudioPlayer Audio) MakePlayer(bool equalizerEnabled)
    {
        var audio = new FakeAudioPlayer();
        var player = new PlayerViewModel(
            audio, new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());

        var settings = new SettingsViewModel(
            new TestPersistenceService(), new FakeLibraryService(), new NoOpPlayHistoryService())
        {
            EqualizerEnabled = equalizerEnabled,
            SelectedEqPresetName = "Flat",
            ReplayGainMode = "Off",
            SoundCheckEnabled = false,
            CrossfadeEnabled = false,
        };
        player.SetSettingsViewModel(settings);

        player.CurrentTrack = new Track
        {
            Title = "Probe",
            Codec = "FLAC",
            FilePath = "probe.flac",
            SampleRate = 44100,
            BitsPerSample = 16,
        };
        return (player, audio);
    }

    private sealed class NoOpPlayHistoryService : Noctis.Services.IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static SignalPathStage EqStage(PlayerViewModel p) =>
        Assert.Single(p.SignalPathStages, s => s.Stage == "Equalizer");

    [AvaloniaFact]
    public void EqualizerEnabledButFlat_StaysLossless()
    {
        var (player, audio) = MakePlayer(equalizerEnabled: true);
        audio.EqualizerActive = false; // Flat curve → bypassed in the player

        player.RefreshSignalPath();

        Assert.Equal("Lossless", player.SignalPathQuality);
        Assert.False(EqStage(player).IsActive);
        Assert.Equal("Flat — bypass", EqStage(player).Detail);
    }

    [AvaloniaFact]
    public void EqualizerActuallyShapingTheCurve_CountsAsDsp()
    {
        var (player, audio) = MakePlayer(equalizerEnabled: true);
        audio.EqualizerActive = true; // user moved a band off flat

        player.RefreshSignalPath();

        Assert.Equal("Enhanced", player.SignalPathQuality);
        Assert.True(EqStage(player).IsActive);
        Assert.Equal("Flat", EqStage(player).Detail);
    }

    [AvaloniaFact]
    public void EqualizerToggledOff_ReadsOff()
    {
        var (player, audio) = MakePlayer(equalizerEnabled: false);
        audio.EqualizerActive = false;

        player.RefreshSignalPath();

        Assert.Equal("Lossless", player.SignalPathQuality);
        Assert.Equal("Off", EqStage(player).Detail);
    }
}
