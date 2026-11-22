using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord request 11-21 ("save the last conversion settings"): the Convert dialog opens with
/// the options of the last conversion started, across restarts. A saved value the dialog no
/// longer offers falls back to its default; a reset forgets them.
/// </summary>
public class AudioConverterPrefsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noctis-convprefs-" + Guid.NewGuid().ToString("N"));

    public AudioConverterPrefsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class FakeConverter : IAudioConverterService
    {
        public AudioConvertOptions? LastOptions;
        public string? GetFfmpegPath() => "ffmpeg";
        public Task<string?> ValidateFfmpegAsync(string? path = null, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<ConvertSummary> ConvertAsync(IReadOnlyList<Track> tracks, AudioConvertOptions options,
            IProgress<ConvertProgress> progress, CancellationToken ct)
        {
            LastOptions = options;
            return Task.FromResult(new ConvertSummary());
        }
    }

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static Track[] Tracks(int bits = 16) => new[]
    {
        new Track
        {
            Id = Guid.NewGuid(), Title = "Song", Artist = "Artist", Album = "Album",
            FilePath = TestPaths.Primary("Music", "song.flac"), BitsPerSample = bits,
        },
    };

    private AudioConverterPrefs Custom() => new(
        "flac", 256, "24", "24", "DSD128", _root, "%album% - %title%",
        CopyTags: false, EmbedArtwork: false, AddToLibrary: false);

    [Fact]
    public void SavedPrefs_OpenTheDialogWithThem()
    {
        var vm = new AudioConverterViewModel(Tracks(), new FakeConverter(), new FakeLibraryService(), Custom());

        Assert.Equal("flac", vm.SelectedFormat);
        Assert.Equal(256, vm.SelectedBitrate);
        Assert.Equal("24", vm.SelectedBitDepth);
        Assert.Equal("24", vm.SelectedPcmBitDepth);
        Assert.Equal("DSD128", vm.SelectedDsdRate);
        Assert.Equal(_root, vm.OutputFolder);
        Assert.Equal("%album% - %title%", vm.FilenamePattern);
        Assert.False(vm.CopyTags);
        Assert.False(vm.EmbedArtwork);
        Assert.False(vm.AddToLibrary);
        Assert.False(vm.OverwriteExisting);
    }

    [Fact]
    public void InvalidSavedValues_FallBackToTheDefaults()
    {
        var gone = Path.Combine(_root, "gone");
        var prefs = new AudioConverterPrefs("mp2", 999, "32", "8", "DSD1024", gone, "%artist%: %title%",
            CopyTags: true, EmbedArtwork: true, AddToLibrary: true);
        var fresh = new AudioConverterViewModel(Tracks(), new FakeConverter(), new FakeLibraryService());
        var vm = new AudioConverterViewModel(Tracks(), new FakeConverter(), new FakeLibraryService(), prefs);

        Assert.Equal(fresh.SelectedFormat, vm.SelectedFormat);
        Assert.Equal(fresh.SelectedBitrate, vm.SelectedBitrate);
        Assert.Equal(fresh.SelectedBitDepth, vm.SelectedBitDepth);
        Assert.Equal(fresh.SelectedPcmBitDepth, vm.SelectedPcmBitDepth);
        Assert.Equal(fresh.SelectedDsdRate, vm.SelectedDsdRate);
        Assert.Equal(string.Empty, vm.OutputFolder);
        Assert.Equal(fresh.FilenamePattern, vm.FilenamePattern);
        Assert.True(vm.StartCommand.CanExecute(null));

        // A relative folder or a blank pattern is not taken either.
        var vm2 = new AudioConverterViewModel(Tracks(), new FakeConverter(), new FakeLibraryService(),
            prefs with { OutputFolder = "Music", FilenamePattern = "  " });
        Assert.Equal(string.Empty, vm2.OutputFolder);
        Assert.Equal(fresh.FilenamePattern, vm2.FilenamePattern);
    }

    [Fact]
    public void FreshSettings_GiveTheDialogsOwnDefaults()
    {
        var fresh = new AudioConverterViewModel(Tracks(24), new FakeConverter(), new FakeLibraryService());
        var vm = new AudioConverterViewModel(Tracks(24), new FakeConverter(), new FakeLibraryService(),
            MetadataHelper.ConverterPrefsFrom(new AppSettings()));

        Assert.Equal(fresh.CurrentPrefs(), vm.CurrentPrefs() with { PcmBitDepth = fresh.CurrentPrefs().PcmBitDepth });
        // No saved WAV/AIFF depth: a 24-bit source still keeps its depth.
        Assert.Equal("24", vm.SelectedPcmBitDepth);
    }

    [Fact]
    public async Task Start_SavesTheRunsOptions()
    {
        var saved = new List<AudioConverterPrefs>();
        var vm = new AudioConverterViewModel(Tracks(), new FakeConverter(), new FakeLibraryService(), savePrefs: saved.Add)
        {
            SelectedFormat = "opus", SelectedBitrate = 160, OutputFolder = _root,
            FilenamePattern = "%title%", CopyTags = false, AddToLibrary = false, OverwriteExisting = true,
        };

        await vm.StartCommand.ExecuteAsync(null);

        var p = Assert.Single(saved);
        Assert.Equal("opus", p.Format);
        Assert.Equal(160, p.BitrateKbps);
        Assert.Equal(_root, p.OutputFolder);
        Assert.Equal("%title%", p.FilenamePattern);
        Assert.False(p.CopyTags);
        Assert.False(p.AddToLibrary);
        // Not a WAV/AIFF run: the source-based depth in the box is not taken as a choice.
        Assert.Equal(string.Empty, p.PcmBitDepth);

        // A save that throws never stops the run.
        var fake = new FakeConverter();
        var failing = new AudioConverterViewModel(Tracks(), fake, new FakeLibraryService(),
            savePrefs: _ => throw new IOException("disk full"));
        await failing.StartCommand.ExecuteAsync(null);
        Assert.NotNull(fake.LastOptions);
    }

    [Fact]
    public async Task PcmDepth_IsSavedOnlyByAWavOrAiffRun()
    {
        var saved = new List<AudioConverterPrefs>();
        var vm = new AudioConverterViewModel(Tracks(24), new FakeConverter(), new FakeLibraryService(),
            savePrefs: saved.Add) { SelectedFormat = "wav", SelectedPcmBitDepth = "16" };
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal("16", saved[^1].PcmBitDepth);

        vm.SelectedFormat = "mp3";
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal("16", saved[^1].PcmBitDepth);
    }

    [Fact]
    public async Task Prefs_RoundTripThroughSettings_FolderInThisComputersSlot()
    {
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        var settings = new AppSettings();
        MetadataHelper.ApplyConverterPrefs(settings, Custom());
        settings.ExtensionData = new() { ["someFutureSetting"] = System.Text.Json.JsonDocument.Parse("42").RootElement.Clone() };
        await persistence.SaveSettingsAsync(settings);

        var back = await new PersistenceService(Path.Combine(_root, "data")).LoadSettingsAsync();
        Assert.Equal(Custom(), MetadataHelper.ConverterPrefsFrom(back));
        Assert.True(back.ExtensionData!.ContainsKey("someFutureSetting"));

        var computers = await File.ReadAllTextAsync(Path.Combine(_root, "data", ComputerStore.FileName));
        Assert.Contains("converterOutputFolder", computers);
        Assert.Contains(PersistenceService.ComputerLocalSettings, n => n == nameof(AppSettings.ConverterOutputFolder));
    }

    [AvaloniaFact]
    public async Task Reset_ForgetsTheSavedOptions()
    {
        var persistence = new PersistenceService(Path.Combine(_root, "reset"));
        var settings = new AppSettings();
        MetadataHelper.ApplyConverterPrefs(settings, Custom());
        await persistence.SaveSettingsAsync(settings);
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistoryService());
        vm.SetAudioPlayer(new FakeAudioPlayer());
        await vm.LoadAsync();
        Assert.Equal(Custom(), MetadataHelper.ConverterPrefsFrom(vm.GetSettings()));

        var defaults = vm.CreateResetSettings();
        await vm.WriteDefaultSettingsAsync(defaults);
        vm.ResetSettingsToDefaults(defaults);
        await vm.SaveAsync();

        var fresh = MetadataHelper.ConverterPrefsFrom(new AppSettings());
        Assert.Equal(fresh, MetadataHelper.ConverterPrefsFrom(vm.GetSettings()));
        Assert.Equal(fresh, MetadataHelper.ConverterPrefsFrom(await persistence.LoadSettingsAsync()));
    }
}
