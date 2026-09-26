using System.Reflection;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Settings › Reset everything promises every setting returns to its default. The reset
/// writes a default settings.json, but the next save re-applies the view-model's
/// properties on top of it (SyncToSettings), so any property the reset did not put back
/// (upmix, shortcuts, accent-follows-artwork, music videos, per-song lyrics clips, the
/// Songs columns, Lyrics Studio, ...) was written straight back over the defaults.
/// </summary>
public class SettingsResetTests : IDisposable
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

    private static List<string> DifferingProperties(AppSettings actual, AppSettings expected) =>
        typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .Where(p => JsonSerializer.Serialize(p.GetValue(actual)) != JsonSerializer.Serialize(p.GetValue(expected)))
            .Select(p => p.Name)
            .ToList();

    /// <summary>What a fresh install writes on its first save.</summary>
    private async Task<AppSettings> FreshInstallSaveAsync()
    {
        var persistence = new PersistenceService(Path.Combine(_root, "fresh"));
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistoryService());
        await vm.LoadAsync();
        await vm.SaveAsync();
        return await persistence.LoadSettingsAsync();
    }

    [AvaloniaFact]
    public async Task Reset_ThenNextSave_WritesTheSameFileAsAFreshInstall()
    {
        var dataDir = Path.Combine(_root, "used");
        Directory.CreateDirectory(dataDir);
        var clip = Path.Combine(dataDir, "clip.mp4");
        File.WriteAllText(clip, "x");

        var persistence = new PersistenceService(dataDir);
        await persistence.SaveSettingsAsync(new AppSettings
        {
            CommunityPluginsEnabled = false,
            AccentFollowsArtwork = true,
            UpmixMode = "Surround",
            Shortcuts = new Dictionary<string, string> { [nameof(ShortcutAction.PlayPause)] = "P" },
            LyricsBackgroundMediaOverrides = new Dictionary<string, string> { ["track:1"] = clip },
            LyricsBackgroundPausesWithPlayback = true,
            MusicVideosEnabled = false,
            MusicVideoRoundedCorners = false,
            DiscordShowAlbum = false,
            NoctisServerPort = 5050,
            ShowGenreColumn = false,
            ShowBpmColumn = true,
            ShowPlaysColumn = false,
            SyncEnabled = true,
            SyncDeviceName = "Desk",
            YouTubeDownloadFolder = Path.Combine(dataDir, "yt"),
            YtDlpPath = Path.Combine(dataDir, "yt-dlp.exe"),
            LyricsStudioModel = "Small",
            LyricsStudioLanguage = "en",
            LyricsStudioWordTimings = false,
            LyricsStudioSkipAlreadyTimed = false,
            LyricsStudioEmbedTags = true,
            LyricsStudioOnlineLyrics = false,
        });

        var audio = new FakeAudioPlayer();
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistoryService());
        vm.SetAudioPlayer(audio);
        await vm.LoadAsync();
        Assert.True(vm.AccentFollowsArtwork);
        Assert.False(vm.ShortcutService.IsDefault(ShortcutAction.PlayPause));
        Assert.True(vm.HasLyricsBackgroundOverride("track:1"));
        Assert.Equal("Surround", audio.UpmixMode);

        // ConfirmResetLibrary's settings half, without its OS and data-folder side effects.
        var defaults = new AppSettings { CommunityPluginsEnabled = false };
        await persistence.SaveSettingsAsync(defaults);
        vm.ResetSettingsToDefaults(defaults);

        Assert.True(vm.ShortcutService.IsDefault(ShortcutAction.PlayPause));
        Assert.False(vm.HasLyricsBackgroundOverride("track:1"));
        Assert.Equal("Off", audio.UpmixMode);

        // Any toggle, or the unconditional save when the window closes.
        await vm.SaveAsync();
        var afterReset = await persistence.LoadSettingsAsync();

        Assert.Empty(DifferingProperties(afterReset, await FreshInstallSaveAsync()));
    }
}
