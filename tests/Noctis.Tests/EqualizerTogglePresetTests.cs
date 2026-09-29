using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #94 (EQ on/off switch on Settings, the player bar and the mini player — without
/// losing the curve) and #95 (save / recall / delete EQ presets, built-ins included).
/// </summary>
public class EqualizerTogglePresetTests : IDisposable
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

    private static async Task<(SettingsViewModel Vm, FakeAudioPlayer Audio)> CreateLoadedAsync(IPersistenceService? persistence = null)
    {
        var vm = new SettingsViewModel(persistence ?? new TestPersistenceService(), new FakeLibraryService(), new NoOpPlayHistoryService());
        await vm.LoadAsync();
        var audio = new FakeAudioPlayer();
        vm.SetAudioPlayer(audio);
        return (vm, audio);
    }

    private SettingsViewModel CreateOnDisk() =>
        new(new PersistenceService(_root), new FakeLibraryService(), new NoOpPlayHistoryService());

    private static double[] Gains(SettingsViewModel vm) => vm.EqBands.Select(b => b.GainDb).ToArray();

    // ── #94: master switch ──

    [AvaloniaFact]
    public async Task SwitchingOff_BypassesButKeepsTheCurve_AndOnRestoresItExactly()
    {
        var (vm, audio) = await CreateLoadedAsync();
        vm.EqBands[2].GainDb = 6;
        vm.EqPreampDb = -3;
        var shaped = audio.LastEqualizer!.Value;
        var gainsBefore = Gains(vm);
        Assert.True(shaped.Enabled);

        vm.EqualizerEnabled = false;
        Assert.False(audio.LastEqualizer!.Value.Enabled);
        Assert.Equal(gainsBefore, Gains(vm));
        Assert.Equal(-3, vm.EqPreampDb);
        Assert.Equal("Custom", vm.SelectedEqPresetName);
        Assert.Equal(0.5, vm.EqualizerControlsOpacity);

        vm.EqualizerEnabled = true;
        var restored = audio.LastEqualizer!.Value;
        Assert.True(restored.Enabled);
        Assert.Equal(shaped.Bands, restored.Bands);
        Assert.Equal(shaped.PreampDb, restored.PreampDb);
        Assert.Equal(1.0, vm.EqualizerControlsOpacity);
    }

    [AvaloniaFact]
    public async Task PlayerBarToggle_FlipsTheSameSwitch_AndNotifies()
    {
        var (vm, _) = await CreateLoadedAsync();
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        player.SetSettingsViewModel(vm);
        var notified = 0;
        player.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PlayerViewModel.IsEqualizerEnabled)) notified++; };

        Assert.True(player.IsEqualizerEnabled);
        player.ToggleEqualizerCommand.Execute(null);
        Assert.False(vm.EqualizerEnabled);
        Assert.False(player.IsEqualizerEnabled);

        vm.ToggleEqualizerCommand.Execute(null); // mini player path
        Assert.True(player.IsEqualizerEnabled);
        Assert.Equal(2, notified);
    }

    [AvaloniaFact]
    public async Task PerTrackPreset_IsBypassedWhileOff_AndComesBackWhenSwitchedOn()
    {
        var (vm, audio) = await CreateLoadedAsync();
        vm.EqBands[0].GainDb = 6;
        Assert.True(vm.SaveUserEqPreset("Track curve"));
        vm.EqBands[0].GainDb = -6; // global curve now differs from the preset
        var presetCurve = ParametricEqMath.MapToGraphicBands(
            vm.EqBands.Select((b, i) => new ParametricEqBand { FrequencyHz = b.FrequencyHz, GainDb = i == 0 ? 6 : b.GainDb, Q = b.Q }));

        vm.EqualizerEnabled = false;
        vm.ApplyEqPresetByName("Track curve");
        Assert.False(audio.LastEqualizer!.Value.Enabled);
        vm.ApplyEqPresetByName("Rock"); // built-in override too
        Assert.False(audio.LastEqualizer!.Value.Enabled);

        vm.ApplyEqPresetByName("Track curve");
        vm.EqualizerEnabled = true; // mid-track: the track's own preset, not the global curve
        Assert.True(audio.LastEqualizer!.Value.Enabled);
        Assert.Equal(presetCurve, audio.LastEqualizer!.Value.Bands);

        vm.ApplyEqPresetByName(null); // next track has no override → global curve
        Assert.Equal(ParametricEqMath.MapToGraphicBands(vm.EqBands.Select(b =>
            new ParametricEqBand { FrequencyHz = b.FrequencyHz, GainDb = b.GainDb, Q = b.Q })), audio.LastEqualizer!.Value.Bands);
    }

    [AvaloniaFact]
    public async Task StoppingPlayback_DropsTheTrackPreset_SoOnDoesNotRePushIt()
    {
        var (vm, audio) = await CreateLoadedAsync();
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        player.SetSettingsViewModel(vm);
        vm.EqBands[0].GainDb = 6;
        Assert.True(vm.SaveUserEqPreset("Track curve"));
        vm.SelectedEqPresetName = "Custom";
        vm.EqBands[0].GainDb = -6; // global curve differs from the track's preset
        var global = ParametricEqMath.MapToGraphicBands(vm.EqBands.Select(b =>
            new ParametricEqBand { FrequencyHz = b.FrequencyHz, GainDb = b.GainDb, Q = b.Q }));

        player.CurrentTrack = new Track { Title = "Tagged", FilePath = "t.flac", EqPreset = "Track curve" };
        vm.ApplyEqPresetByName("Track curve");
        player.CurrentTrack = null; // stop / cleared queue

        vm.EqualizerEnabled = false;
        vm.EqualizerEnabled = true;
        Assert.Equal(global, audio.LastEqualizer!.Value.Bands);
    }

    // ── Leaving a built-in keeps the level (review: preamp fold) ──

    private static void AssertSameLevel(float expected, float actual) =>
        Assert.True(Math.Abs(expected - actual) < 0.01f, $"applied preamp moved: {expected} → {actual}");

    /// <summary>Built-in curves come from LibVLC's preset table (the native libvlc ships in
    /// the test output). Without it every built-in falls back to the Custom path and the
    /// preamp tests below would pass without testing anything.</summary>
    private static void EnsureLibVlc() => LibVLCSharp.Shared.Core.Initialize();

    [AvaloniaFact]
    public async Task DeletingTheSelectedBuiltIn_KeepsTheAppliedPreamp()
    {
        EnsureLibVlc();
        var (vm, audio) = await CreateLoadedAsync();
        vm.SelectedEqPresetName = "Rock";
        var before = audio.LastEqualizer!.Value.PreampDb;
        // Rock's own VLC preamp sits below unity — otherwise this test would prove nothing.
        Assert.True(Math.Abs(before - ParametricEqMath.VlcEqUnityPreampDb) > 0.5f, $"Rock preamp {before}");

        vm.DeleteSelectedEqPresetCommand.Execute(null);

        Assert.Equal("Custom", vm.SelectedEqPresetName);
        AssertSameLevel(before, audio.LastEqualizer!.Value.PreampDb);
    }

    [AvaloniaFact]
    public async Task SavingFromABuiltIn_KeepsTheAppliedPreamp_AndRecallMatches()
    {
        EnsureLibVlc();
        var (vm, audio) = await CreateLoadedAsync();
        vm.SelectedEqPresetName = "Pop";
        vm.EqPreampDb = -2; // a user pre-amp riding on the built-in folds in too
        var before = audio.LastEqualizer!.Value.PreampDb;
        Assert.True(Math.Abs(before - (ParametricEqMath.VlcEqUnityPreampDb - 2)) > 0.5f, $"Pop preamp {before}");

        Assert.True(vm.SaveUserEqPreset("My Pop"));
        AssertSameLevel(before, audio.LastEqualizer!.Value.PreampDb);

        vm.SelectedEqPresetName = "Flat";
        vm.SelectedEqPresetName = "My Pop";
        AssertSameLevel(before, audio.LastEqualizer!.Value.PreampDb);
    }

    [AvaloniaFact]
    public async Task EditingABandOnABuiltIn_KeepsThePreampLevel()
    {
        EnsureLibVlc();
        var (vm, audio) = await CreateLoadedAsync();
        vm.SelectedEqPresetName = "Club";
        var before = audio.LastEqualizer!.Value.PreampDb;
        Assert.True(Math.Abs(before - ParametricEqMath.VlcEqUnityPreampDb) > 0.5f, $"Club preamp {before}");

        vm.EqBands[5].GainDb += 0.1;

        Assert.Equal("Custom", vm.SelectedEqPresetName);
        AssertSameLevel(before, audio.LastEqualizer!.Value.PreampDb);
    }

    [AvaloniaFact]
    public async Task SavingFromFlat_IsAFlatUnityCurve()
    {
        var (vm, audio) = await CreateLoadedAsync();
        vm.SelectedEqPresetName = "Flat";

        Assert.True(vm.SaveUserEqPreset("Plain"));

        Assert.Equal(0, vm.EqPreampDb);
        Assert.All(vm.EqBands, b => Assert.Equal(0, b.GainDb));
        // Flat played as a bypass (native level); the saved preset plays through the EQ at unity.
        AssertSameLevel(ParametricEqMath.VlcEqUnityPreampDb, audio.LastEqualizer!.Value.PreampDb);
        Assert.All(audio.LastEqualizer!.Value.Bands, g => Assert.Equal(0f, g));
    }

    [AvaloniaFact]
    public async Task SignalPath_ReadsOffWhenSwitchedOff_AndNamesAUserPreset()
    {
        var (vm, _) = await CreateLoadedAsync();
        var audio = new FakeAudioPlayer { EqualizerActive = true };
        var player = new PlayerViewModel(audio, new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        player.SetSettingsViewModel(vm);
        player.CurrentTrack = new Track { Title = "Probe", Codec = "FLAC", FilePath = "probe.flac", SampleRate = 44100, BitsPerSample = 16 };
        vm.EqBands[1].GainDb = 4;
        Assert.True(vm.SaveUserEqPreset("Monitors"));

        player.RefreshSignalPath();
        Assert.Equal("Monitors", Assert.Single(player.SignalPathStages, s => s.Stage == "Equalizer").Detail);

        vm.EqualizerEnabled = false;
        audio.EqualizerActive = false;
        player.RefreshSignalPath();
        Assert.Equal("Off", Assert.Single(player.SignalPathStages, s => s.Stage == "Equalizer").Detail);
    }

    // ── #95: user presets ──

    [AvaloniaFact]
    public async Task SavedPreset_IsListedSelected_AndRecallsBandsAndPreampExactly()
    {
        var (vm, _) = await CreateLoadedAsync();
        vm.EqBands[0].GainDb = 5;
        vm.EqBands[3].Q = 3.3;
        vm.EqPreampDb = -4;
        var saved = Gains(vm);

        Assert.True(vm.SaveUserEqPreset("  Headphones fix  "));
        Assert.Equal("Headphones fix", vm.VisibleEqPresets.Last());
        Assert.Equal("Headphones fix", vm.SelectedEqPresetName);
        Assert.Equal(0, vm.SelectedEqPresetIndex);
        Assert.True(vm.CanDeleteSelectedEqPreset);
        Assert.Equal("", vm.NewEqPresetName);

        // Edit → Custom; the preset itself is untouched.
        vm.EqBands[0].GainDb = -8;
        vm.EqPreampDb = 2;
        Assert.Equal("Custom", vm.SelectedEqPresetName);

        vm.SelectedEqPresetName = "Headphones fix";
        Assert.Equal(saved, Gains(vm));
        Assert.Equal(3.3, vm.EqBands[3].Q);
        Assert.Equal(-4, vm.EqPreampDb);
        Assert.Equal(0, vm.SelectedEqPresetIndex);
    }

    [AvaloniaFact]
    public async Task SavingUnderAnExistingUserName_Overwrites()
    {
        var (vm, _) = await CreateLoadedAsync();
        vm.EqBands[0].GainDb = 3;
        Assert.True(vm.SaveUserEqPreset("Mine"));
        vm.EqBands[0].GainDb = 9;
        Assert.True(vm.SaveUserEqPreset("MINE"));

        Assert.Equal(new[] { "MINE" }, vm.UserEqPresetNames.ToArray());
        Assert.Single(vm.VisibleEqPresets, n => string.Equals(n, "mine", StringComparison.OrdinalIgnoreCase));
        vm.SelectedEqPresetName = "Flat";
        vm.SelectedEqPresetName = "MINE";
        Assert.Equal(9, vm.EqBands[0].GainDb);
    }

    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Flat")]
    [InlineData("custom")]
    [InlineData("ROCK")]
    [InlineData("None")]
    [InlineData("12345678901234567890123456789012345678901")] // 41 chars
    public async Task InvalidNames_AreRefusedWithAReason(string name)
    {
        var (vm, _) = await CreateLoadedAsync();
        var listed = vm.VisibleEqPresets.ToList();

        Assert.False(vm.SaveUserEqPreset(name));
        Assert.NotEqual("", vm.EqPresetSaveError);
        Assert.Empty(vm.UserEqPresetNames);
        Assert.Equal(listed, vm.VisibleEqPresets.ToList());

        vm.NewEqPresetName = "x"; // typing again clears the refusal
        Assert.Equal("", vm.EqPresetSaveError);
    }

    [AvaloniaFact]
    public async Task DeletingAUserPreset_RemovesIt_AndKeepsTheSoundAsCustom()
    {
        var (vm, audio) = await CreateLoadedAsync();
        vm.EqBands[0].GainDb = 7;
        Assert.True(vm.SaveUserEqPreset("Gone soon"));
        var before = audio.LastEqualizer!.Value;
        var gains = Gains(vm);

        vm.DeleteSelectedEqPresetCommand.Execute(null);

        Assert.DoesNotContain("Gone soon", vm.VisibleEqPresets);
        Assert.Empty(vm.UserEqPresetNames);
        Assert.Equal("Custom", vm.SelectedEqPresetName);
        Assert.Equal(gains, Gains(vm));
        Assert.Equal(before.Bands, audio.LastEqualizer!.Value.Bands);
    }

    [AvaloniaFact]
    public async Task BuiltIns_AreHiddenOnDelete_AndRestoredInPlace_ButCustomAndFlatStay()
    {
        var (vm, _) = await CreateLoadedAsync();
        var original = vm.VisibleEqPresets.ToList();

        vm.SelectedEqPresetName = "Flat";
        Assert.False(vm.CanDeleteSelectedEqPreset);
        vm.DeleteSelectedEqPresetCommand.Execute(null);
        Assert.Contains("Flat", vm.VisibleEqPresets);

        vm.SelectedEqPresetName = "Custom";
        Assert.False(vm.CanDeleteSelectedEqPreset);

        vm.SelectedEqPresetName = "Rock";
        Assert.True(vm.CanDeleteSelectedEqPreset);
        vm.DeleteSelectedEqPresetCommand.Execute(null);
        Assert.DoesNotContain("Rock", vm.VisibleEqPresets);
        Assert.True(vm.HasHiddenEqPresets);
        Assert.Equal("Custom", vm.SelectedEqPresetName);

        vm.RestoreBuiltInEqPresetsCommand.Execute(null);
        Assert.Equal(original, vm.VisibleEqPresets.ToList());
        Assert.False(vm.HasHiddenEqPresets);
        Assert.Equal("Custom", vm.SelectedEqPresetName);
    }

    // ── Persistence ──

    [AvaloniaFact]
    public async Task PresetsHiddenBuiltInsSelectionAndSwitch_SurviveARestart()
    {
        var vm = CreateOnDisk();
        await vm.LoadAsync();
        vm.EqBands[4].GainDb = -5;
        vm.EqPreampDb = -2;
        Assert.True(vm.SaveUserEqPreset("Studio"));
        vm.SelectedEqPresetName = "Techno";
        vm.DeleteSelectedEqPresetCommand.Execute(null);
        vm.SelectedEqPresetName = "Studio";
        vm.EqualizerEnabled = false;
        await vm.SaveAsync();

        var reloaded = CreateOnDisk();
        await reloaded.LoadAsync();

        Assert.False(reloaded.EqualizerEnabled);
        Assert.Equal("Studio", reloaded.SelectedEqPresetName);
        Assert.Equal(0, reloaded.SelectedEqPresetIndex);
        Assert.Equal(new[] { "Studio" }, reloaded.UserEqPresetNames.ToArray());
        Assert.DoesNotContain("Techno", reloaded.VisibleEqPresets);
        Assert.Equal("Studio", reloaded.VisibleEqPresets.Last());
        Assert.Equal(-5, reloaded.EqBands[4].GainDb);
        Assert.Equal(-2, reloaded.EqPreampDb);

        var stored = reloaded.GetSettings();
        Assert.Equal(new[] { "Techno" }, stored.HiddenEqPresets.ToArray());
        Assert.Equal("Studio", stored.SelectedUserEqPreset);
        Assert.Equal(-1, stored.EqualizerPresetIndex);
        Assert.Equal(-2, Assert.Single(stored.UserEqPresets).PreampDb);
    }

    [AvaloniaFact]
    public async Task Load_FallsBackToCustom_WhenTheSelectedPresetIsGone_AndDropsInvalidEntries()
    {
        var seeded = new AppSettings
        {
            EqualizerPresetIndex = -1,
            SelectedUserEqPreset = "Deleted elsewhere",
            UserEqPresets =
            {
                new UserEqPreset { Name = "Kept", PreampDb = 1, Bands = { new ParametricEqBand { FrequencyHz = 100, GainDb = 3, Q = 1 } } },
                new UserEqPreset { Name = "Flat" },  // collides with a built-in
                new UserEqPreset { Name = "kept" },  // duplicate
                new UserEqPreset { Name = "   " },
            },
            HiddenEqPresets = { "Flat", "Custom", "Nope", "pop" },
        };
        await new PersistenceService(_root).SaveSettingsAsync(seeded);

        var vm = CreateOnDisk();
        await vm.LoadAsync();

        Assert.Equal("Custom", vm.SelectedEqPresetName);
        Assert.Equal(new[] { "Kept" }, vm.UserEqPresetNames.ToArray());
        Assert.Contains("Flat", vm.VisibleEqPresets);
        Assert.Contains("Custom", vm.VisibleEqPresets);
        Assert.DoesNotContain("Pop", vm.VisibleEqPresets);
        Assert.True(vm.HasHiddenEqPresets);
    }

    [AvaloniaFact]
    public async Task Load_ASelectedBuiltInThatWasHidden_BecomesCustom()
    {
        var seeded = new AppSettings
        {
            EqualizerPresetIndex = Array.IndexOf(SettingsViewModel.EqPresetNames, "Rock") - 1,
            HiddenEqPresets = { "Rock" },
        };
        await new PersistenceService(_root).SaveSettingsAsync(seeded);

        var vm = CreateOnDisk();
        await vm.LoadAsync();

        Assert.Equal("Custom", vm.SelectedEqPresetName);
        Assert.Equal(0, vm.SelectedEqPresetIndex);
    }

    [AvaloniaFact]
    public async Task SettingsWithoutThePresetFields_LoadWithTheBuiltInsOnly()
    {
        // Files written before #95 have no UserEqPresets / HiddenEqPresets at all.
        await new PersistenceService(_root).SaveSettingsAsync(new AppSettings
        {
            UserEqPresets = null!,
            HiddenEqPresets = null!,
            EqualizerPresetIndex = Array.IndexOf(SettingsViewModel.EqPresetNames, "Pop") - 1,
        });

        var vm = CreateOnDisk();
        await vm.LoadAsync();

        Assert.Equal(SettingsViewModel.EqPresetNames, vm.VisibleEqPresets.ToArray());
        Assert.Equal("Pop", vm.SelectedEqPresetName);
        Assert.Empty(vm.UserEqPresetNames);
        Assert.False(vm.HasHiddenEqPresets);
    }
}
