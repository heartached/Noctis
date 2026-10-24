using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Settings → Output → Output Device: the picker's option list (pure builder) and the
/// setting's persistence (saved by SettingsViewModel, read back by Program.Main's
/// launch-time reader before the audio player exists).
/// </summary>
public class OutputDevicePickerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    // ── AudioOutputDeviceCatalog.Build (pure ordering/dedup, no COM) ──

    [Fact]
    public void CatalogBuild_PutsDefaultFirst_ThenOrdersByName()
    {
        var devices = new[]
        {
            ("id-speakers", "Speakers"),
            ("id-dac", "USB DAC"),
            ("id-hdmi", "HDMI"),
        };
        var built = AudioOutputDeviceCatalog.Build(devices, "id-dac");

        Assert.Equal(3, built.Count);
        Assert.Equal("id-dac", built[0].Id);
        Assert.True(built[0].IsDefault);
        Assert.False(built[1].IsDefault);
        // Name order for the rest, ordinal case-insensitive.
        Assert.Equal("HDMI", built[1].Name);
        Assert.Equal("Speakers", built[2].Name);
    }

    [Fact]
    public void CatalogBuild_DedupesIds_AndDisambiguatesDuplicateNames()
    {
        var devices = new[]
        {
            ("id-1", "USB DAC"),
            ("id-1", "USB DAC"),      // same endpoint twice — one entry
            ("id-2", "USB DAC"),      // two identical DACs — "(2)" suffix
        };
        var built = AudioOutputDeviceCatalog.Build(devices, null);

        Assert.Equal(2, built.Count);
        Assert.Equal("USB DAC", built[0].Name);
        Assert.Equal("USB DAC (2)", built[1].Name);
    }

    // ── SettingsViewModel.BuildOutputDeviceOptions (labels are culture-dependent;
    //    structure is not) ──

    private static Services.AudioOutputDevice Dev(string id, string name, bool isDefault = false) =>
        new(id, name, isDefault);

    [Fact]
    public void OptionList_StartsWithSystemDefault_AndMarksTheDefaultEndpoint()
    {
        var options = SettingsViewModel.BuildOutputDeviceOptions(
            new[] { Dev("id-a", "Speakers", isDefault: true), Dev("id-b", "DAC") },
            selectedId: "", knownMissingName: null);

        Assert.Equal(3, options.Count);
        Assert.Equal("", options[0].Id);
        Assert.False(options[0].IsMissing);
        Assert.False(string.IsNullOrEmpty(options[0].Label));
        // The Windows-default endpoint carries the marker; the other does not.
        Assert.StartsWith("Speakers", options[1].Label, StringComparison.Ordinal);
        Assert.True(options[1].Label.Length > "Speakers".Length);
        Assert.Equal("DAC", options[2].Label);
    }

    [Fact]
    public void OptionList_KeepsADisconnectedPin_AsAMissingRow()
    {
        var options = SettingsViewModel.BuildOutputDeviceOptions(
            new[] { Dev("id-a", "Speakers", isDefault: true) },
            selectedId: "id-gone", knownMissingName: "Studio DAC");

        Assert.Equal(3, options.Count);
        var missing = Assert.Single(options, o => o.IsMissing);
        Assert.Equal("id-gone", missing.Id);
        Assert.Contains("Studio DAC", missing.Label);
    }

    [Fact]
    public void OptionList_NoMissingRow_WhenThePinIsConnected()
    {
        var options = SettingsViewModel.BuildOutputDeviceOptions(
            new[] { Dev("id-a", "Speakers"), Dev("id-dac", "Studio DAC") },
            selectedId: "id-dac", knownMissingName: null);

        Assert.Equal(3, options.Count);
        Assert.DoesNotContain(options, o => o.IsMissing);
        Assert.Equal("id-dac", options[^1].Id);
    }

    // ── Persistence ──

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private SettingsViewModel CreateViewModel() => new(
        new PersistenceService(_root), new FakeLibraryService(), new NoOpPlayHistoryService());

    [AvaloniaFact]
    public async Task OutputDeviceId_RoundTripsThroughSaveAndReload()
    {
        // Seed a settings.json with a pinned endpoint, the way an earlier session
        // (or a hand edit) leaves it.
        var seeded = new AppSettings { AudioOutputDeviceId = "id-dac" };
        await new PersistenceService(_root).SaveSettingsAsync(seeded);

        var vm = CreateViewModel();
        await vm.LoadAsync();

        if (!OperatingSystem.IsWindows())
        {
            // The picker is Windows-only; other platforms must still persist "".
            Assert.Equal("", PersistenceService.ReadStringSettingAtLaunch(_root,
                nameof(AppSettings.AudioOutputDeviceId)));
            return;
        }

        // The pin is loaded as the selection, kept as a "(not connected)" row when the
        // endpoint is absent, and written back unchanged by a save.
        Assert.NotNull(vm.SelectedOutputDevice);
        Assert.Equal("id-dac", vm.SelectedOutputDevice!.Id);
        await vm.SaveAsync();
        Assert.Equal("id-dac",
            PersistenceService.ReadStringSettingAtLaunch(_root, nameof(AppSettings.AudioOutputDeviceId)));

        // Switching to "System Default" persists the empty pin.
        vm.SelectedOutputDevice = vm.OutputDeviceOptions.First(o => o.Id.Length == 0);
        await vm.SaveAsync();
        Assert.Equal("",
            PersistenceService.ReadStringSettingAtLaunch(_root, nameof(AppSettings.AudioOutputDeviceId)));
    }

    [Fact]
    public async Task ReadStringSettingAtLaunch_MissingKeyReadsEmpty()
    {
        await new PersistenceService(_root).SaveSettingsAsync(new AppSettings());
        Assert.Equal("", PersistenceService.ReadStringSettingAtLaunch(_root, "NoSuchSetting"));
        Assert.Equal("", PersistenceService.ReadStringSettingAtLaunch(
            Path.Combine(_root, "absent"), nameof(AppSettings.AudioOutputDeviceId)));
    }
}
